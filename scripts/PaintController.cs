using Godot;
using System;
using System.Linq;

namespace OpenCanvas3D;

// Proof-of-concept for the core paint mechanic: raycast the mouse against
// the model's real mesh triangles, find the hit point's UV coordinate via
// barycentric interpolation, and draw a stroke into a SubViewport-backed
// texture that's bound as the model's own albedo — so the paint shows up
// directly on the model's surface, in real time, with no extra texture
// upload step (the SubViewport's ViewportTexture IS the live material).
public partial class PaintController : Node3D
{
    private enum BrushType { Marker, Pencil, Calligraphy }

    // Each paintable mesh gets its own canvas: its own SubViewport, its own
    // 2D draw surface, its own material bound to that viewport's texture,
    // and its own undo/redo history. Without this, every mesh bound to a
    // single shared canvas would show the exact same pixels — painting one
    // placed shape would paint the same UV coordinates on the model and
    // every other shape at once, which is exactly the bug this fixes.
    private sealed class PaintTarget
    {
        // A target can bind more than one mesh (an imported model can be
        // several sub-meshes that should still act as one paintable asset)
        // but always has exactly one canvas — that's what makes it a single
        // paintable unit distinct from every other target.
        public required System.Collections.Generic.List<MeshInstance3D> MeshInstances;
        public required SubViewport CanvasViewport;
        public required Node2D Canvas2D;
        public readonly System.Collections.Generic.List<HistoryEntry> UndoStack = new();
        public readonly System.Collections.Generic.List<HistoryEntry> RedoStack = new();
        public HistoryEntry? PendingHistoryEntry;
    }

    private Camera3D _camera = null!;
    private Node3D _modelRoot = null!;
    private readonly System.Collections.Generic.List<PaintTarget> _paintTargets = new();
    private PaintTarget? _activeTarget;
    private FileDialog? _importDialog;
    private TextureRect _canvas2DOverlay = null!;
    private bool _is2DMode;
    private Label _hintLabel = null!;
    private Label _toolLabel = null!;
    private Label _brushLabel = null!;
    private Control _toolbar = null!;
    private Control _sidePanel = null!;
    private Button _viewToggleButton = null!;
    private Label _thicknessLabel = null!;
    private Label _opacityLabel = null!;
    private Label _brushSectionLabel = null!;
    private GridContainer _brushGrid = null!;
    private Label _shape2DSectionLabel = null!;
    private GridContainer _shape2DGrid = null!;
    private Label _shape3DSectionLabel = null!;
    private GridContainer _shape3DGrid = null!;
    private readonly System.Collections.Generic.Dictionary<Shape2DType, Button> _shape2DButtons = new();
    private readonly System.Collections.Generic.Dictionary<Shape3DType, Button> _shape3DButtons = new();
    private Label _thicknessSectionLabel = null!;
    private ColorRect _currentColorSwatch = null!;
    private readonly System.Collections.Generic.Dictionary<BrushType, Button> _brushTypeButtons = new();

    private Vector2? _lastUv;
    private Vector2? _lastOrbitMouse;
    private Color _brushColor = new(0.0f, 0.47f, 0.83f);
    private int _brushSize = 18;
    private float _brushOpacity = 1.0f;
    private BrushType _brushType = BrushType.Marker;
    private enum Tool { Brush, Eraser, Fill, Eyedropper, Doodle, Text, Shape2D, Shape3D }
    private Tool _activeTool = Tool.Brush;
    private bool _eraserEnabled => _activeTool == Tool.Eraser;
    private bool _eyedropperActive => _activeTool == Tool.Eyedropper;
    private bool _fillEnabled => _activeTool == Tool.Fill;
    private bool _doodleEnabled => _activeTool == Tool.Doodle;
    private bool _textEnabled => _activeTool == Tool.Text;
    private bool _shape2DEnabled => _activeTool == Tool.Shape2D;
    private bool _shape3DEnabled => _activeTool == Tool.Shape3D;
    private enum Shape2DType { Rectangle, Circle, Line }
    private enum Shape3DType { Cube, Sphere, Cone, Cylinder, Capsule, Torus }
    private Shape2DType _shape2DType = Shape2DType.Rectangle;
    private Shape3DType _shape3DType = Shape3DType.Cube;
    private LineEdit? _textInput;
    private Vector2? _shape2DStart;
    private Vector2? _shape2DEnd;
    private Node2D? _shape2DPreviewNode;
    private Node3D? _shape3DRoot;
    private readonly System.Collections.Generic.List<Vector3> _doodlePoints = new();
    private Node3D? _doodleRoot;
    private MeshInstance3D? _doodlePreview;
    private const float MaxStrokeJumpUv = 0.15f;

    // Undo/redo works on whole-canvas image snapshots rather than diffing
    // individual strokes: the canvas is built from an unbounded mix of
    // vector nodes (Sprite2D dots, Line2D segments, baked fill images), so
    // "undo the last stroke" really means "restore the canvas pixels from
    // right before that stroke started." 3D-space additions (doodles, 3D
    // shapes) aren't canvas pixels at all, so each history entry also
    // remembers which scene nodes it added (to hide on undo, show on redo)
    // and which existing nodes it hid (e.g. Clear hides old doodles instead
    // of freeing them, so they can come back on undo).
    private sealed class HistoryEntry
    {
        public required Image CanvasBefore;
        public required System.Collections.Generic.List<Node3D> AddedNodes;
        public required System.Collections.Generic.List<Node3D> HiddenNodes;
    }
    private const int MaxHistory = 30;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private float _cameraYaw = Mathf.Pi / 4f;
    private float _cameraPitch = 0.55f;
    private float _cameraDistance = 4.7f;
    private float _minCameraDistance = 0.5f;
    private float _maxCameraDistance = 40.0f;
    private Vector3 _cameraTarget = new(0f, 0.45f, 0f);

    private static readonly Color[] PaletteColors =
    {
        new(0.0f, 0.0f, 0.0f), new(1.0f, 1.0f, 1.0f), new(0.5f, 0.5f, 0.5f), new(0.75f, 0.75f, 0.75f),
        new(0.85f, 0.1f, 0.1f), new(1.0f, 0.55f, 0.1f), new(1.0f, 0.85f, 0.1f), new(0.1f, 0.65f, 0.25f),
        new(0.0f, 0.6f, 0.6f), new(0.0f, 0.47f, 0.83f), new(0.35f, 0.2f, 0.7f), new(0.8f, 0.2f, 0.6f),
        new(0.55f, 0.35f, 0.2f), new(0.95f, 0.75f, 0.65f), new(0.2f, 0.2f, 0.25f), new(0.9f, 0.9f, 0.86f),
    };

    private PaintTarget? _modelTarget;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _canvas2DOverlay = GetNode<TextureRect>("UI/Canvas2DOverlay");
        _hintLabel = GetNode<Label>("UI/HintLabel");
        _modelRoot = GetNode<Node3D>("Model");
        _doodleRoot = new Node3D { Name = "Doodles" };
        AddChild(_doodleRoot);
        _shape3DRoot = new Node3D { Name = "Shapes3D" };
        AddChild(_shape3DRoot);

        BuildToolbar();
        UpdateSidePanelForTool();
        UpdateToolLabels();

        GetWindow().FilesDropped += OnFilesDropped;

        var modelViewport = GetNode<SubViewport>("PaintCanvasViewport");
        var modelCanvas2D = GetNode<Node2D>("PaintCanvasViewport/PaintCanvas2D");
        if (!TryBindMesh(_modelRoot, modelViewport, modelCanvas2D, out _modelTarget))
            _hintLabel.Text = "No mesh found in Model — nothing to paint on.";
        else
            SetActiveTarget(_modelTarget!);

        FrameCameraOnModel();
        UpdateCameraOrbit();
    }

    // A model may be made of several MeshInstance3D nodes (body, parts,
    // accessories, ...) — collecting all of them, not just the first found,
    // is what lets imported multi-mesh models be painted on everywhere they
    // are visible instead of only wherever the first mesh happens to be.
    private static void CollectMeshInstances(Node root, System.Collections.Generic.List<MeshInstance3D> into)
    {
        if (root is MeshInstance3D mi) into.Add(mi);
        foreach (Node child in root.GetChildren())
            CollectMeshInstances(child, into);
    }

    // Binds every MeshInstance3D found under `root` to one shared PaintTarget
    // — used for the model (which may be multiple sub-meshes acting as one
    // paintable asset). Reuses the given viewport/canvas2D nodes so the
    // model keeps its scene-authored canvas across re-imports.
    private bool TryBindMesh(Node root, SubViewport viewport, Node2D canvas2D, out PaintTarget? target)
    {
        var meshes = new System.Collections.Generic.List<MeshInstance3D>();
        CollectMeshInstances(root, meshes);
        if (meshes.Count == 0)
        {
            target = null;
            return false;
        }

        target = new PaintTarget
        {
            MeshInstances = meshes,
            CanvasViewport = viewport,
            Canvas2D = canvas2D,
        };
        _paintTargets.Add(target);
        PaintBaseCoat(target);
        BindCanvasAsAlbedo(target);
        return true;
    }

    private void OnFilesDropped(string[] files)
    {
        foreach (var path in files)
        {
            var ext = path.GetExtension().ToLower();
            if (ext is "glb" or "gltf" or "obj")
            {
                ImportModel(path);
                return;
            }
        }
        _hintLabel.Text = "Drop a .glb, .gltf, or .obj model file to import it";
    }

    private void ImportModel(string absolutePath)
    {
        Node3D? importedRoot = null;
        if (absolutePath.GetExtension().ToLower() == "obj")
        {
            var mesh = ResourceLoader.Load<Mesh>(absolutePath, "Mesh", ResourceLoader.CacheMode.Ignore);
            if (mesh != null)
                importedRoot = new MeshInstance3D { Mesh = mesh };
        }
        else
        {
            using var doc = new GltfDocument();
            var state = new GltfState();
            var error = doc.AppendFromFile(absolutePath, state);
            if (error == Error.Ok)
                importedRoot = doc.GenerateScene(state) as Node3D;
        }

        if (importedRoot == null)
        {
            _hintLabel.Text = $"Failed to import model: {absolutePath.GetFile()}";
            return;
        }

        foreach (Node child in _modelRoot.GetChildren())
            child.QueueFree();
        _modelRoot.AddChild(importedRoot);

        var viewport = _modelTarget!.CanvasViewport;
        var canvas2D = _modelTarget.Canvas2D;
        _paintTargets.Remove(_modelTarget);

        if (TryBindMesh(_modelRoot, viewport, canvas2D, out var newTarget))
        {
            _modelTarget = newTarget;
            SetActiveTarget(_modelTarget!);
            FrameCameraOnModel();
            UpdateCameraOrbit();
            _hintLabel.Text = $"Imported {absolutePath.GetFile()}";
        }
        else
        {
            // The imported file had no mesh — keep the previous target alive
            // rather than leaving nothing bound to the (now empty) canvas.
            _paintTargets.Add(_modelTarget);
            _hintLabel.Text = $"Imported file has no mesh: {absolutePath.GetFile()}";
        }
    }

    private void OpenImportDialog()
    {
        if (_importDialog == null)
        {
            _importDialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Import 3D Model",
                Size = new Vector2I(720, 480),
            };
            _importDialog.AddFilter("*.glb,*.gltf,*.obj", "3D Models");
            _importDialog.FileSelected += ImportModel;
            AddChild(_importDialog);
        }
        _importDialog.PopupCentered();
    }

    private void PaintBaseCoat(PaintTarget target)
    {
        foreach (Node child in target.Canvas2D.GetChildren())
            child.QueueFree();

        var bg = new ColorRect
        {
            Color = new Color(0.9f, 0.9f, 0.86f),
            Size = target.CanvasViewport.Size,
        };
        target.Canvas2D.AddChild(bg);
    }

    // Undo/redo is per-target: painting on one shape only ever needs to
    // undo pixels on that shape's own canvas, not the model's or another
    // shape's — each PaintTarget carries its own pending entry and stacks.
    private void BeginHistoryEntry()
    {
        if (_activeTarget is not { } target || target.PendingHistoryEntry != null) return;
        target.PendingHistoryEntry = new HistoryEntry
        {
            CanvasBefore = target.CanvasViewport.GetTexture().GetImage(),
            AddedNodes = new System.Collections.Generic.List<Node3D>(),
            HiddenNodes = new System.Collections.Generic.List<Node3D>(),
        };
    }

    private void TrackAddedNode(Node3D node)
    {
        _activeTarget?.PendingHistoryEntry?.AddedNodes.Add(node);
    }

    // Clear hides existing doodles/shapes rather than freeing them, purely
    // so undo can bring them back — QueueFree'd nodes can't be un-freed.
    private void TrackHiddenNode(Node3D node)
    {
        _activeTarget?.PendingHistoryEntry?.HiddenNodes.Add(node);
        node.Visible = false;
    }

    private void CommitHistoryEntry()
    {
        if (_activeTarget is not { } target || target.PendingHistoryEntry is not { } entry) return;
        target.UndoStack.Add(entry);
        if (target.UndoStack.Count > MaxHistory)
            target.UndoStack.RemoveAt(0);
        target.RedoStack.Clear();
        target.PendingHistoryEntry = null;
        UpdateUndoRedoButtons();
    }

    private void CancelHistoryEntry()
    {
        if (_activeTarget is { } target)
            target.PendingHistoryEntry = null;
    }

    private void Undo()
    {
        if (_activeTarget is not { } target || target.UndoStack.Count == 0) return;
        var entry = target.UndoStack[^1];
        target.UndoStack.RemoveAt(target.UndoStack.Count - 1);

        // Redoing this same action later needs to restore the canvas back
        // to how it looked right after the action ran, so snapshot the
        // current (post-action) pixels into the redo entry before reverting.
        var redoEntry = new HistoryEntry
        {
            CanvasBefore = target.CanvasViewport.GetTexture().GetImage(),
            AddedNodes = entry.AddedNodes,
            HiddenNodes = entry.HiddenNodes,
        };
        foreach (var node in entry.AddedNodes)
        {
            if (IsInstanceValid(node))
                node.Visible = false;
        }
        foreach (var node in entry.HiddenNodes)
        {
            if (IsInstanceValid(node))
                node.Visible = true;
        }
        RestoreCanvasImage(target, entry.CanvasBefore);
        target.RedoStack.Add(redoEntry);
        UpdateUndoRedoButtons();
        _hintLabel.Text = "Undo";
    }

    private void Redo()
    {
        if (_activeTarget is not { } target || target.RedoStack.Count == 0) return;
        var entry = target.RedoStack[^1];
        target.RedoStack.RemoveAt(target.RedoStack.Count - 1);

        var undoEntry = new HistoryEntry
        {
            CanvasBefore = target.CanvasViewport.GetTexture().GetImage(),
            AddedNodes = entry.AddedNodes,
            HiddenNodes = entry.HiddenNodes,
        };
        foreach (var node in entry.AddedNodes)
        {
            if (IsInstanceValid(node))
                node.Visible = true;
        }
        foreach (var node in entry.HiddenNodes)
        {
            if (IsInstanceValid(node))
                node.Visible = false;
        }
        RestoreCanvasImage(target, entry.CanvasBefore);
        target.UndoStack.Add(undoEntry);
        UpdateUndoRedoButtons();
        _hintLabel.Text = "Redo";
    }

    private void RestoreCanvasImage(PaintTarget target, Image image)
    {
        foreach (Node child in target.Canvas2D.GetChildren())
            child.QueueFree();
        var restored = new Sprite2D
        {
            Texture = ImageTexture.CreateFromImage(image),
            Centered = false,
        };
        target.Canvas2D.AddChild(restored);
    }

    private void UpdateUndoRedoButtons()
    {
        if (_undoButton != null)
            _undoButton.Disabled = _activeTarget is not { } t1 || t1.UndoStack.Count == 0;
        if (_redoButton != null)
            _redoButton.Disabled = _activeTarget is not { } t2 || t2.RedoStack.Count == 0;
    }

    // The SubViewport renders into an internal texture every frame; grabbing
    // that as a ViewportTexture and assigning it to a fresh
    // StandardMaterial3D's AlbedoTexture is the whole trick — anything drawn
    // as a 2D child of the SubViewport becomes what that target's mesh(es) show.
    private void BindCanvasAsAlbedo(PaintTarget target)
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = target.CanvasViewport.GetTexture(),
        };
        foreach (var meshInstance in target.MeshInstances)
        {
            var mesh = meshInstance.Mesh;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                meshInstance.SetSurfaceOverrideMaterial(surface, material);
        }
    }

    // Switches which target is being edited and refreshes the undo/redo
    // buttons and 2D-view overlay to match it — called whenever a raycast
    // hits a different mesh, or a new target (shape) is created/selected.
    private void SetActiveTarget(PaintTarget target)
    {
        _activeTarget = target;
        _canvas2DOverlay.Texture = target.CanvasViewport.GetTexture();
        UpdateUndoRedoButtons();
    }

    public override void _Process(double delta)
    {
        if (!_is2DMode)
            HandleCameraOrbit();

        bool mouseDown = Input.IsMouseButtonPressed(MouseButton.Left);

        if (_doodleEnabled && !_is2DMode)
        {
            ProcessDoodle(mouseDown);
            return;
        }

        if (_shape3DEnabled && !_is2DMode)
        {
            ProcessShape3D(mouseDown);
            return;
        }

        Vector2? uv = null;
        if (mouseDown && !PointerIsOverToolbar())
        {
            if (_is2DMode)
            {
                uv = ScreenToUv2D();
            }
            else if (RaycastToUv() is { } hit)
            {
                // Only switch canvases when starting a new stroke/action —
                // switching mid-drag would tear a single stroke across two
                // different targets' undo histories and canvases.
                if (_lastUv == null && hit.target != _activeTarget)
                    SetActiveTarget(hit.target);
                uv = hit.uv;
            }
        }

        if (_textInput != null)
        {
            // A text-entry popup is open — leave canvas input alone until it
            // closes (Enter commits, Escape/focus-out cancels).
            return;
        }

        if (uv is { } current)
        {
            if (_eyedropperActive)
            {
                if (_lastUv == null)
                    PickColorAt(current);
            }
            else if (_fillEnabled)
            {
                if (_lastUv == null)
                {
                    BeginHistoryEntry();
                    FloodFillAt(current);
                    CommitHistoryEntry();
                }
            }
            else if (_textEnabled)
            {
                if (_lastUv == null)
                    OpenTextInput(current);
            }
            else if (_shape2DEnabled)
            {
                if (_lastUv == null)
                    BeginHistoryEntry();
                _shape2DEnd = current;
                _shape2DStart ??= current;
                UpdateShape2DPreview();
            }
            else
            {
                _hintLabel.Text = $"hit uv: {current.X:F3}, {current.Y:F3}";
                if (_lastUv == null)
                    BeginHistoryEntry();
                // A raycast hit can jump to a disconnected UV island between
                // two frames (seams, mesh backfaces) even though the cursor
                // moved smoothly in 3D — connecting those with a full-width
                // line reads as the brush "filling" the canvas. Treat a big
                // UV jump as a new stroke instead of a continuous one.
                if (_lastUv is { } last && last.DistanceTo(current) < MaxStrokeJumpUv)
                    DrawStrokeSegment(last, current);
                else
                    DrawDot(current);
            }
            _lastUv = current;
        }
        else
        {
            _hintLabel.Text = mouseDown
                ? "No hit — aim at the model's surface"
                : "Click and drag on the model to paint";
            if (_lastUv != null && _shape2DEnabled)
                FinalizeShape2D();
            else if (_lastUv != null && !_eyedropperActive && !_fillEnabled && !_textEnabled)
                CommitHistoryEntry();
            _lastUv = null;
        }
    }

    // Flood fill works on a rasterized snapshot of the canvas (the SubViewport
    // is built from vector Sprite2D/Line2D strokes, not a pixel buffer), then
    // bakes the filled result back as a single full-canvas image so future
    // strokes layer on top of it.
    private void FloodFillAt(Vector2 uv)
    {
        if (_activeTarget is not { } target) return;
        var image = target.CanvasViewport.GetTexture().GetImage();
        int width = image.GetWidth();
        int height = image.GetHeight();
        int startX = Mathf.Clamp(Mathf.RoundToInt(uv.X * width), 0, width - 1);
        int startY = Mathf.Clamp(Mathf.RoundToInt(uv.Y * height), 0, height - 1);

        var targetColor = image.GetPixel(startX, startY);
        var fillColor = CurrentPaintColor();
        if (targetColor.IsEqualApprox(fillColor))
            return;

        var stack = new System.Collections.Generic.Stack<(int x, int y)>();
        stack.Push((startX, startY));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || x >= width || y < 0 || y >= height) continue;
            if (!image.GetPixel(x, y).IsEqualApprox(targetColor)) continue;

            image.SetPixel(x, y, fillColor);
            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }

        foreach (Node child in target.Canvas2D.GetChildren())
            child.QueueFree();
        var baked = new Sprite2D
        {
            Texture = ImageTexture.CreateFromImage(image),
            Centered = false,
        };
        target.Canvas2D.AddChild(baked);
        _hintLabel.Text = "Filled";
    }

    private bool _shape3DPlaced;

    // 3D shapes drop a primitive mesh (cube, sphere, ...) at the point the
    // mouse ray hits the model, or a fixed distance in front of the camera
    // if it misses — one shape per mouse press, mirroring Paint 3D's "3D
    // shapes" tab where each click/drag drops a single object into the scene.
    private void ProcessShape3D(bool mouseDown)
    {
        if (!mouseDown)
        {
            _shape3DPlaced = false;
            return;
        }
        if (_shape3DPlaced || PointerIsOverToolbar()) return;

        var mousePos = GetViewport().GetMousePosition();
        var from = _camera.ProjectRayOrigin(mousePos);
        var direction = _camera.ProjectRayNormal(mousePos);
        float planeDistance = _cameraTarget.DistanceTo(_camera.GlobalPosition);
        var point = from + direction * planeDistance;

        // Track the "a shape was added" undo entry against whatever target
        // was active before placing this one, then hand off to the new
        // shape's own target — that's what makes the shape paintable right
        // away without smearing its pixels onto the model or other shapes.
        BeginHistoryEntry();
        var shape = new MeshInstance3D
        {
            Mesh = BuildPrimitiveMesh(_shape3DType),
            Position = point,
        };
        _shape3DRoot!.AddChild(shape);
        TrackAddedNode(shape);
        CommitHistoryEntry();

        var newTarget = CreatePaintTargetForShape(shape);
        SetActiveTarget(newTarget);

        _shape3DPlaced = true;
        _hintLabel.Text = $"{_shape3DType} added — paint it with the Brush tool";
    }

    // Placed shapes each need their own SubViewport + canvas at runtime
    // (unlike the model's, which is authored in the scene) since there's no
    // way to know ahead of time how many shapes will be placed.
    private PaintTarget CreatePaintTargetForShape(MeshInstance3D shape)
    {
        var viewport = new SubViewport
        {
            Size = CanvasSize,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);
        var canvas2D = new Node2D();
        viewport.AddChild(canvas2D);

        var target = new PaintTarget
        {
            MeshInstances = new System.Collections.Generic.List<MeshInstance3D> { shape },
            CanvasViewport = viewport,
            Canvas2D = canvas2D,
        };
        _paintTargets.Add(target);
        PaintBaseCoat(target);
        BindCanvasAsAlbedo(target);
        return target;
    }

    private Mesh BuildPrimitiveMesh(Shape3DType type)
    {
        float scale = Mathf.Max(0.02f, _brushSize / 256f);
        return type switch
        {
            Shape3DType.Sphere => new SphereMesh { Radius = scale, Height = scale * 2f },
            Shape3DType.Cone => new CylinderMesh { TopRadius = 0f, BottomRadius = scale, Height = scale * 2f },
            Shape3DType.Cylinder => new CylinderMesh { TopRadius = scale, BottomRadius = scale, Height = scale * 2f },
            Shape3DType.Capsule => new CapsuleMesh { Radius = scale, Height = scale * 3f },
            Shape3DType.Torus => new TorusMesh { InnerRadius = scale * 0.5f, OuterRadius = scale },
            _ => new BoxMesh { Size = Vector3.One * scale * 2f },
        };
    }

    // Doodle draws in actual 3D space rather than on a model's UVs: the mouse
    // path is projected onto a plane facing the camera at a fixed distance
    // from it, collected as a polyline, and extruded into a real tube mesh
    // that is rebuilt every frame while dragging — so the shape grows live
    // as you draw, the same way Paint 3D's "3D doodle" tool behaves — and
    // finalized (left in the scene) when the stroke ends.
    private void ProcessDoodle(bool mouseDown)
    {
        if (!mouseDown || PointerIsOverToolbar())
        {
            if (_doodlePoints.Count > 1)
            {
                // The in-progress preview mesh becomes the finished doodle —
                // just stop tracking it so the next stroke starts a new one.
                TrackAddedNode(_doodlePreview!);
                CommitHistoryEntry();
                _doodlePreview = null;
                _hintLabel.Text = "Doodle added";
            }
            else
            {
                CancelHistoryEntry();
                _doodlePreview?.QueueFree();
                _doodlePreview = null;
                _hintLabel.Text = "Doodle: drag in the scene to draw a 3D shape";
            }
            _doodlePoints.Clear();
            return;
        }

        if (_doodlePoints.Count == 0)
            BeginHistoryEntry();

        var mousePos = GetViewport().GetMousePosition();
        var from = _camera.ProjectRayOrigin(mousePos);
        var direction = _camera.ProjectRayNormal(mousePos);
        float planeDistance = _cameraTarget.DistanceTo(_camera.GlobalPosition);
        var cameraForward = -_camera.GlobalTransform.Basis.Z;
        var planePoint = _camera.GlobalPosition + cameraForward * planeDistance;

        float denom = direction.Dot(cameraForward);
        if (Mathf.Abs(denom) < 1e-6f) return;
        float t = (planePoint - from).Dot(cameraForward) / denom;
        if (t < 0) return;
        var point = from + direction * t;

        if (_doodlePoints.Count == 0 || _doodlePoints[^1].DistanceTo(point) > 0.02f)
        {
            _doodlePoints.Add(point);
            _hintLabel.Text = $"Doodle: {_doodlePoints.Count} points";
            if (_doodlePoints.Count > 1)
                UpdateDoodlePreview();
        }
    }

    private void UpdateDoodlePreview()
    {
        var mesh = BuildTubeMesh(_doodlePoints);
        if (_doodlePreview == null)
        {
            _doodlePreview = new MeshInstance3D
            {
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = CurrentPaintColor(),
                    VertexColorUseAsAlbedo = true,
                },
            };
            _doodleRoot!.AddChild(_doodlePreview);
        }
        _doodlePreview.Mesh = mesh;
    }

    private Mesh BuildTubeMesh(System.Collections.Generic.List<Vector3> points)
    {
        const int radialSegments = 8;
        float tubeRadius = Mathf.Max(0.005f, _brushSize / 1024f);

        var surfaceTool = new SurfaceTool();
        surfaceTool.Begin(Mesh.PrimitiveType.Triangles);
        surfaceTool.SetColor(CurrentPaintColor());

        // Each ring's right/up frame is carried forward from the previous
        // ring (parallel transport) rather than recomputed from a fixed
        // world "up" every time — recomputing independently lets the frame
        // flip sign whenever the tangent crosses near the reference axis,
        // which twists the tube's winding into the banded, inside-out look.
        var rings = new System.Collections.Generic.List<Vector3[]>();
        Vector3? previousRight = null;
        for (int i = 0; i < points.Count; i++)
        {
            var forward = (i < points.Count - 1 ? points[i + 1] - points[i] : points[i] - points[i - 1]).Normalized();

            Vector3 right;
            if (previousRight is { } prevRight)
            {
                // Re-orthogonalize the carried-over right vector against the
                // new tangent instead of snapping back to a world axis.
                var up = forward.Cross(prevRight).Normalized();
                right = up.Cross(forward).Normalized();
            }
            else
            {
                var seedUp = Mathf.Abs(forward.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
                right = forward.Cross(seedUp).Normalized();
            }
            var ringUp = right.Cross(forward).Normalized();
            previousRight = right;

            var ring = new Vector3[radialSegments];
            for (int s = 0; s < radialSegments; s++)
            {
                float angle = Mathf.Tau * s / radialSegments;
                ring[s] = points[i] + (right * Mathf.Cos(angle) + ringUp * Mathf.Sin(angle)) * tubeRadius;
            }
            rings.Add(ring);
        }

        for (int i = 0; i < rings.Count - 1; i++)
        {
            var a = rings[i];
            var b = rings[i + 1];
            for (int s = 0; s < radialSegments; s++)
            {
                int next = (s + 1) % radialSegments;
                surfaceTool.AddVertex(a[s]);
                surfaceTool.AddVertex(b[s]);
                surfaceTool.AddVertex(a[next]);

                surfaceTool.AddVertex(a[next]);
                surfaceTool.AddVertex(b[s]);
                surfaceTool.AddVertex(b[next]);
            }
        }

        surfaceTool.GenerateNormals();
        return surfaceTool.Commit();
    }

    // Text stamps a string onto the canvas at the clicked UV: a floating
    // LineEdit opens over the click point for entry, and Enter bakes the
    // typed text into the canvas as a Label sized by the current brush size.
    private void OpenTextInput(Vector2 uv)
    {
        var ui = GetNode<CanvasLayer>("UI");
        _textInput = new LineEdit
        {
            PlaceholderText = "Type text, Enter to place",
            CustomMinimumSize = new Vector2(220, 32),
        };
        var mouse = GetViewport().GetMousePosition();
        _textInput.Position = mouse;
        ui.AddChild(_textInput);
        _textInput.GrabFocus();

        _textInput.TextSubmitted += text =>
        {
            if (!string.IsNullOrWhiteSpace(text))
                PlaceText(text, uv);
            CloseTextInput();
        };
        _textInput.FocusExited += CloseTextInput;
    }

    private void CloseTextInput()
    {
        _textInput?.QueueFree();
        _textInput = null;
        _lastUv = null;
    }

    private void PlaceText(string text, Vector2 uv)
    {
        if (_activeTarget is not { } target) return;
        BeginHistoryEntry();
        var label = new Label
        {
            Text = text,
            Position = UvToCanvasPixels(uv),
            Modulate = CurrentPaintColor(),
        };
        label.AddThemeFontSizeOverride("font_size", Mathf.Clamp(_brushSize * 2, 12, 200));
        target.Canvas2D.AddChild(label);
        CommitHistoryEntry();
        _hintLabel.Text = "Text added";
    }

    private void PickColorAt(Vector2 uv)
    {
        if (_activeTarget is not { } target) return;
        var image = target.CanvasViewport.GetTexture().GetImage();
        int x = Mathf.Clamp(Mathf.RoundToInt(uv.X * image.GetWidth()), 0, image.GetWidth() - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(uv.Y * image.GetHeight()), 0, image.GetHeight() - 1);
        _brushColor = image.GetPixel(x, y);
        SelectTool(Tool.Brush);
        _hintLabel.Text = $"Picked color #{_brushColor.ToHtml(false)}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } wheel)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp)
            {
                _cameraDistance = Mathf.Max(_minCameraDistance, _cameraDistance * 0.88f);
                UpdateCameraOrbit();
            }
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraDistance = Mathf.Min(_maxCameraDistance, _cameraDistance * 1.12f);
                UpdateCameraOrbit();
            }
        }
        else if (@event is InputEventKey { Pressed: true, CtrlPressed: true } key)
        {
            if (key.Keycode == Key.Z)
                Undo();
            else if (key.Keycode == Key.Y)
                Redo();
        }
    }

    // Paint 3D palette: a near-white app chrome (title bar + ribbon are both
    // the same light gray, distinguished only by a hairline), a light-blue
    // selection/active tint, and a white side panel — quite different from
    // this app's earlier dark-ribbon look.
    private static readonly Color TitleBarBg = new(0.976f, 0.976f, 0.980f);
    private static readonly Color RibbonBg = new(0.918f, 0.925f, 0.937f);
    private static readonly Color OptionsRowBg = new(0.965f, 0.968f, 0.973f);
    private static readonly Color PanelBg = new(1.0f, 1.0f, 1.0f);
    private static readonly Color AccentBlue = new(0.0f, 0.47f, 0.83f);
    private static readonly Color AccentBlueTint = new(0.831f, 0.902f, 0.965f);
    private static readonly Color TileBg = new(0.965f, 0.968f, 0.973f);
    private static readonly Color TileBgActive = new(0.831f, 0.902f, 0.965f);
    private static readonly Color TextDark = new(0.157f, 0.165f, 0.184f);
    private static readonly Color TextMuted = new(0.404f, 0.416f, 0.443f);
    private static readonly Color TextLight = TextDark;
    private const int SidePanelWidth = 260;

    private void BuildToolbar()
    {
        var ui = GetNode<CanvasLayer>("UI");

        // --- Title bar: app name, mirroring Paint 3D's "Untitled - Paint 3D".
        var titleBar = new PanelContainer { Name = "TitleBar", CustomMinimumSize = new Vector2(0, 32) };
        titleBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        titleBar.OffsetBottom = 32;
        titleBar.AddThemeStyleboxOverride("panel", SolidStyleBox(TitleBarBg));
        ui.AddChild(titleBar);
        var titleLabel = new Label
        {
            Text = "Untitled — OpenCanvas 3D",
            Modulate = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLabel.OffsetLeft = 12;
        titleBar.AddChild(titleLabel);

        var titleBarRight = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        titleBarRight.AddThemeConstantOverride("separation", 4);
        titleBarRight.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        titleBarRight.OffsetLeft = 8;
        titleBarRight.OffsetRight = -8;
        titleBar.AddChild(titleBarRight);
        _undoButton = MakeActionButton("Undo", Undo);
        _undoButton.Disabled = true;
        titleBarRight.AddChild(_undoButton);
        _redoButton = MakeActionButton("Redo", Redo);
        _redoButton.Disabled = true;
        titleBarRight.AddChild(_redoButton);

        // --- Ribbon: icon-style tool tabs with a label underneath each.
        _toolbar = new PanelContainer { Name = "Ribbon", CustomMinimumSize = new Vector2(0, 64) };
        _toolbar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _toolbar.OffsetTop = 32;
        _toolbar.OffsetBottom = 96;
        _toolbar.AddThemeStyleboxOverride("panel", SolidStyleBox(RibbonBg));
        ui.AddChild(_toolbar);

        var ribbonRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        ribbonRow.AddThemeConstantOverride("separation", 2);
        ribbonRow.OffsetLeft = 8;
        ribbonRow.OffsetTop = 4;
        _toolbar.AddChild(ribbonRow);

        ribbonRow.AddChild(MakeRibbonTab("Brush", () => SelectTool(Tool.Brush)));
        ribbonRow.AddChild(MakeRibbonTab("Eraser", () => SelectTool(Tool.Eraser)));
        ribbonRow.AddChild(MakeRibbonTab("Fill", () => SelectTool(Tool.Fill)));
        ribbonRow.AddChild(MakeRibbonTab("Eyedropper", () => SelectTool(Tool.Eyedropper)));
        ribbonRow.AddChild(MakeRibbonTab("Doodle", () => SelectTool(Tool.Doodle)));
        ribbonRow.AddChild(MakeRibbonTab("Text", () => SelectTool(Tool.Text)));
        ribbonRow.AddChild(MakeRibbonTab("2D shapes", () => SelectTool(Tool.Shape2D)));
        ribbonRow.AddChild(MakeRibbonTab("3D shapes", () => SelectTool(Tool.Shape3D)));
        ribbonRow.AddChild(new VSeparator { CustomMinimumSize = new Vector2(1, 48) });
        ribbonRow.AddChild(MakeRibbonTab("Clear", () =>
        {
            if (_activeTarget is { } target)
            {
                BeginHistoryEntry();
                PaintBaseCoat(target);
                foreach (Node child in _doodleRoot!.GetChildren().Concat(_shape3DRoot!.GetChildren()))
                {
                    if (child is Node3D node3D && node3D.Visible)
                        TrackHiddenNode(node3D);
                }
                CommitHistoryEntry();
            }
            _lastUv = null;
            _doodlePoints.Clear();
            _doodlePreview = null;
            _hintLabel.Text = "Canvas cleared";
        }));
        ribbonRow.AddChild(MakeRibbonTab("Export", ExportPaintTexture));
        ribbonRow.AddChild(new VSeparator { CustomMinimumSize = new Vector2(1, 48) });
        ribbonRow.AddChild(MakeRibbonTab("Import\nModel", OpenImportDialog));

        // --- Options row: view mode toggle, mirroring Paint 3D's "3D view" control.
        var optionsRow = new PanelContainer { Name = "OptionsRow", CustomMinimumSize = new Vector2(0, 40) };
        optionsRow.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        optionsRow.OffsetTop = 96;
        optionsRow.OffsetBottom = 136;
        optionsRow.AddThemeStyleboxOverride("panel", SolidStyleBox(OptionsRowBg));
        ui.AddChild(optionsRow);

        var optionsInner = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        optionsInner.AddThemeConstantOverride("separation", 8);
        optionsInner.OffsetLeft = 8;
        optionsInner.OffsetTop = 4;
        optionsInner.OffsetRight = -8;
        optionsInner.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        optionsRow.AddChild(optionsInner);

        _viewToggleButton = MakeActionButton("3D view", ToggleViewMode);
        optionsInner.AddChild(_viewToggleButton);

        // --- Right side panel: brush grid, thickness/opacity, color + swatches.
        _sidePanel = new PanelContainer { Name = "SidePanel", CustomMinimumSize = new Vector2(SidePanelWidth, 0) };
        _sidePanel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _sidePanel.OffsetLeft = -SidePanelWidth;
        _sidePanel.OffsetTop = 136;
        _sidePanel.OffsetRight = 0;
        _sidePanel.OffsetBottom = 0;
        _sidePanel.AddThemeStyleboxOverride("panel", SolidStyleBox(PanelBg));
        ui.AddChild(_sidePanel);

        var panelColumn = new VBoxContainer();
        panelColumn.AddThemeConstantOverride("separation", 10);
        panelColumn.OffsetLeft = 16;
        panelColumn.OffsetTop = 14;
        panelColumn.OffsetRight = -16;
        _sidePanel.AddChild(panelColumn);

        _toolLabel = new Label
        {
            Text = "Brush",
            Modulate = AccentBlue,
            ThemeTypeVariation = "HeaderMedium",
        };
        panelColumn.AddChild(_toolLabel);

        _brushSectionLabel = MakeSectionLabel("Brushes");
        panelColumn.AddChild(_brushSectionLabel);
        _brushGrid = new GridContainer { Columns = 4 };
        _brushGrid.AddThemeConstantOverride("h_separation", 8);
        _brushGrid.AddThemeConstantOverride("v_separation", 8);
        panelColumn.AddChild(_brushGrid);
        foreach (BrushType type in Enum.GetValues(typeof(BrushType)))
            _brushGrid.AddChild(MakeBrushTile(type));

        _shape2DSectionLabel = MakeSectionLabel("Shapes");
        panelColumn.AddChild(_shape2DSectionLabel);
        _shape2DGrid = new GridContainer { Columns = 4 };
        _shape2DGrid.AddThemeConstantOverride("h_separation", 8);
        _shape2DGrid.AddThemeConstantOverride("v_separation", 8);
        panelColumn.AddChild(_shape2DGrid);
        foreach (Shape2DType type in Enum.GetValues(typeof(Shape2DType)))
            _shape2DGrid.AddChild(MakeShape2DTile(type));

        _shape3DSectionLabel = MakeSectionLabel("3D Shapes");
        panelColumn.AddChild(_shape3DSectionLabel);
        _shape3DGrid = new GridContainer { Columns = 4 };
        _shape3DGrid.AddThemeConstantOverride("h_separation", 8);
        _shape3DGrid.AddThemeConstantOverride("v_separation", 8);
        panelColumn.AddChild(_shape3DGrid);
        foreach (Shape3DType type in Enum.GetValues(typeof(Shape3DType)))
            _shape3DGrid.AddChild(MakeShape3DTile(type));

        _thicknessSectionLabel = MakeSectionLabel("Thickness");
        panelColumn.AddChild(_thicknessSectionLabel);
        _thicknessLabel = new Label { Text = $"{_brushSize}px", Modulate = TextMuted };
        panelColumn.AddChild(_thicknessLabel);
        var thicknessSlider = new HSlider
        {
            MinValue = 2,
            MaxValue = 64,
            Step = 1,
            Value = _brushSize,
            CustomMinimumSize = new Vector2(0, 24),
        };
        thicknessSlider.ValueChanged += value =>
        {
            _brushSize = Mathf.RoundToInt((float)value);
            UpdateToolLabels();
        };
        panelColumn.AddChild(thicknessSlider);

        panelColumn.AddChild(MakeSectionLabel("Opacity"));
        _opacityLabel = new Label { Text = "100%", Modulate = TextMuted };
        panelColumn.AddChild(_opacityLabel);
        var opacitySlider = new HSlider
        {
            MinValue = 5,
            MaxValue = 100,
            Step = 1,
            Value = _brushOpacity * 100f,
            CustomMinimumSize = new Vector2(0, 24),
        };
        opacitySlider.ValueChanged += value =>
        {
            _brushOpacity = (float)value / 100f;
            UpdateToolLabels();
        };
        panelColumn.AddChild(opacitySlider);

        panelColumn.AddChild(new HSeparator());

        var colorRow = new HBoxContainer();
        colorRow.AddThemeConstantOverride("separation", 8);
        panelColumn.AddChild(colorRow);
        _currentColorSwatch = new ColorRect
        {
            Color = _brushColor,
            CustomMinimumSize = new Vector2(40, 40),
        };
        colorRow.AddChild(_currentColorSwatch);
        colorRow.AddChild(MakeActionButton("Pick", () => SelectTool(Tool.Eyedropper)));

        var palette = new GridContainer { Columns = 4 };
        palette.AddThemeConstantOverride("h_separation", 6);
        palette.AddThemeConstantOverride("v_separation", 6);
        panelColumn.AddChild(palette);
        foreach (var color in PaletteColors)
            palette.AddChild(MakeColorButton(color));

        _hintLabel.OffsetTop = 144;
        _hintLabel.OffsetBottom = 168;
        _hintLabel.OffsetLeft = 12;
        _hintLabel.OffsetRight = -SidePanelWidth - 16;
        _hintLabel.Modulate = TextMuted;
    }

    private static StyleBoxFlat SolidStyleBox(Color color)
    {
        return new StyleBoxFlat { BgColor = color };
    }

    private Label MakeSectionLabel(string text)
    {
        return new Label
        {
            Text = text,
            Modulate = TextDark,
            ThemeTypeVariation = "HeaderSmall",
        };
    }

    // Paint 3D's ribbon buttons stack a glyph over a small caption label
    // rather than showing a single inline text string — approximated here
    // with a two-line, center-aligned button since this app has no icon set.
    private Button MakeRibbonTab(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(64, 56),
            Flat = true,
            Alignment = HorizontalAlignment.Center,
        };
        button.AddThemeColorOverride("font_color", TextDark);
        button.AddThemeColorOverride("font_hover_color", TextDark);
        button.AddThemeColorOverride("font_pressed_color", AccentBlue);
        button.AddThemeStyleboxOverride("hover", SolidStyleBox(AccentBlueTint));
        button.AddThemeStyleboxOverride("pressed", SolidStyleBox(AccentBlueTint));
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += action;
        return button;
    }

    private Button MakeBrushTile(BrushType type)
    {
        var button = new Button
        {
            Text = type.ToString(),
            CustomMinimumSize = new Vector2(48, 40),
        };
        button.AddThemeColorOverride("font_color", TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(TileBg));
        _brushTypeButtons[type] = button;
        button.Pressed += () =>
        {
            _brushType = type;
            SelectTool(Tool.Brush);
        };
        return button;
    }

    private Button MakeShape2DTile(Shape2DType type)
    {
        var button = new Button
        {
            Text = type.ToString(),
            CustomMinimumSize = new Vector2(48, 40),
        };
        button.AddThemeColorOverride("font_color", TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(TileBg));
        _shape2DButtons[type] = button;
        button.Pressed += () =>
        {
            _shape2DType = type;
            SelectTool(Tool.Shape2D);
        };
        return button;
    }

    private Button MakeShape3DTile(Shape3DType type)
    {
        var button = new Button
        {
            Text = type.ToString(),
            CustomMinimumSize = new Vector2(48, 40),
        };
        button.AddThemeColorOverride("font_color", TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(TileBg));
        _shape3DButtons[type] = button;
        button.Pressed += () =>
        {
            _shape3DType = type;
            SelectTool(Tool.Shape3D);
        };
        return button;
    }

    // Each tool controls a different subset of the shared panel — brush type
    // only means something for Brush, the shape grids only for their shape
    // tools — so the relevant section is shown and the rest hidden rather
    // than exposing controls that don't do anything for the current tool.
    private void UpdateSidePanelForTool()
    {
        bool isBrushLike = _activeTool is Tool.Brush or Tool.Eraser;
        _brushSectionLabel.Visible = isBrushLike;
        _brushGrid.Visible = isBrushLike;
        _shape2DSectionLabel.Visible = _shape2DEnabled;
        _shape2DGrid.Visible = _shape2DEnabled;
        _shape3DSectionLabel.Visible = _shape3DEnabled;
        _shape3DGrid.Visible = _shape3DEnabled;
        _thicknessSectionLabel.Text = _doodleEnabled ? "Tube Radius" : _shape3DEnabled ? "Size" : "Thickness";
    }

    private void SelectTool(Tool tool)
    {
        // Switching tools mid-action (mid-stroke, mid-shape-drag) shouldn't
        // silently discard whatever was already drawn — commit any pending
        // history first so it stays on the undo stack, then reset per-tool
        // in-progress state for whichever tool is being left.
        CommitHistoryEntry();

        _activeTool = tool;
        _lastUv = null;
        if (tool != Tool.Doodle && _doodlePoints.Count <= 1)
            _doodlePreview?.QueueFree();
        _doodlePreview = null;
        _doodlePoints.Clear();
        if (tool != Tool.Shape2D)
        {
            _shape2DPreviewNode?.QueueFree();
            _shape2DPreviewNode = null;
            _shape2DStart = null;
            _shape2DEnd = null;
        }
        _textInput?.QueueFree();
        _textInput = null;
        UpdateSidePanelForTool();

        // Doodle and 3D shapes draw in 3D scene space, not on the flat canvas
        // texture, so neither makes sense in 2D view — switch back to 3D
        // automatically rather than silently doing nothing.
        if ((tool == Tool.Doodle || tool == Tool.Shape3D) && _is2DMode)
            SetViewMode(is2D: false);

        UpdateToolLabels();
    }

    private void ToggleViewMode()
    {
        bool goingTo2D = !_is2DMode;
        if (goingTo2D && (_doodleEnabled || _shape3DEnabled))
            SelectTool(Tool.Brush);
        SetViewMode(goingTo2D);
    }

    private void SetViewMode(bool is2D)
    {
        _is2DMode = is2D;
        _canvas2DOverlay.Visible = _is2DMode;
        _lastUv = null;
        _viewToggleButton.Text = _is2DMode ? "2D view" : "3D view";
        _hintLabel.Text = _is2DMode
            ? "2D canvas — draw directly on the flat texture"
            : "Click and drag on the model to paint";
    }

    private Button MakeActionButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(82, 36),
        };
        button.AddThemeColorOverride("font_color", TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(TileBg));
        button.Pressed += action;
        return button;
    }

    private Button MakeColorButton(Color color)
    {
        var button = new Button
        {
            Text = "",
            CustomMinimumSize = new Vector2(32, 32),
            TooltipText = $"Color {color.ToHtml(false)}",
        };
        var style = new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16,
            CornerRadiusBottomRight = 16,
            BorderColor = new Color(0.8f, 0.82f, 0.85f),
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
        };
        button.AddThemeStyleboxOverride("normal", style);
        button.AddThemeStyleboxOverride("hover", style);
        button.AddThemeStyleboxOverride("pressed", style);
        button.Pressed += () =>
        {
            _brushColor = color;
            if (_activeTool is Tool.Eraser or Tool.Eyedropper)
                SelectTool(Tool.Brush);
            else
                UpdateToolLabels();
        };
        return button;
    }

    private void UpdateToolLabels()
    {
        if (_thicknessLabel != null)
            _thicknessLabel.Text = $"{_brushSize}px";
        if (_opacityLabel != null)
            _opacityLabel.Text = $"{Mathf.RoundToInt(_brushOpacity * 100f)}%";
        if (_currentColorSwatch != null)
            _currentColorSwatch.Color = _brushColor;
        foreach (var (type, button) in _brushTypeButtons)
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _brushType ? TileBgActive : TileBg));
        foreach (var (type, button) in _shape2DButtons)
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _shape2DType ? TileBgActive : TileBg));
        foreach (var (type, button) in _shape3DButtons)
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _shape3DType ? TileBgActive : TileBg));

        if (_toolLabel != null)
        {
            _toolLabel.Text = _activeTool switch
            {
                Tool.Eyedropper => "Tool: Eyedropper",
                Tool.Fill => "Tool: Fill",
                Tool.Doodle => "Tool: Doodle",
                Tool.Text => "Tool: Text",
                Tool.Shape2D => $"Tool: {_shape2DType}",
                Tool.Shape3D => $"Tool: {_shape3DType}",
                Tool.Eraser => "Tool: Eraser",
                _ => $"Tool: {_brushType} #{_brushColor.ToHtml(false)}",
            };
        }
    }

    private bool PointerIsOverToolbar()
    {
        var mouse = GetViewport().GetMousePosition();
        if (_toolbar != null && _toolbar.GetGlobalRect().HasPoint(mouse)) return true;
        if (_sidePanel != null && _sidePanel.GetGlobalRect().HasPoint(mouse)) return true;
        return false;
    }

    // 2D mode paints the canvas texture directly, like a flat image editor —
    // the mouse position is mapped straight into UV space through the
    // overlay's own rect instead of raycasting against the 3D model.
    private Vector2? ScreenToUv2D()
    {
        // The overlay keeps the canvas texture's 1:1 aspect ratio, so unless
        // the control itself is exactly square, the texture is letterboxed
        // (centered with empty bars) inside the control's rect — mapping
        // clicks against the full control rect instead of the actual drawn
        // image area is what made 2D-mode painting land off from the cursor.
        var controlRect = _canvas2DOverlay.GetGlobalRect();
        var textureSize = _canvas2DOverlay.Texture?.GetSize() ?? controlRect.Size;
        if (textureSize.X <= 0 || textureSize.Y <= 0) return null;

        float scale = Mathf.Min(controlRect.Size.X / textureSize.X, controlRect.Size.Y / textureSize.Y);
        var drawnSize = textureSize * scale;
        var drawnOrigin = controlRect.Position + (controlRect.Size - drawnSize) * 0.5f;
        var imageRect = new Rect2(drawnOrigin, drawnSize);

        var mouse = GetViewport().GetMousePosition();
        if (!imageRect.HasPoint(mouse)) return null;
        var local = mouse - imageRect.Position;
        return new Vector2(local.X / imageRect.Size.X, local.Y / imageRect.Size.Y);
    }

    // Manual triangle raycast + barycentric UV, mirroring the same approach
    // used for the MAKO prototype: Godot's physics raycast only tells you
    // WHERE in 3D space a ray hit a collision shape, not the UV of the
    // render mesh at that point, so the mesh's own vertex/UV/index arrays
    // are walked directly instead of going through the physics engine.
    // Returns both the UV hit and which PaintTarget owns the mesh that was
    // hit, so the caller can switch the active canvas to match whatever the
    // cursor is actually over before drawing into it.
    private (Vector2 uv, PaintTarget target)? RaycastToUv()
    {
        var mousePos = GetViewport().GetMousePosition();
        var from = _camera.ProjectRayOrigin(mousePos);
        var direction = _camera.ProjectRayNormal(mousePos);

        float bestDist = float.MaxValue;
        Vector2? bestUv = null;
        PaintTarget? bestTarget = null;

        foreach (var target in _paintTargets)
        {
            foreach (var meshInstance in target.MeshInstances)
            {
                if (!meshInstance.Visible) continue;

                var mesh = meshInstance.Mesh;
                var meshTransform = meshInstance.GlobalTransform;

                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    var arrays = mesh.SurfaceGetArrays(surface);
                    var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                    var uvs = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
                    var indicesVariant = arrays[(int)Mesh.ArrayType.Index];
                    if (uvs == null) continue;

                    int[]? indices = indicesVariant.VariantType == Variant.Type.Nil
                        ? null
                        : (int[])indicesVariant;
                    int triCount = indices != null ? indices.Length / 3 : vertices.Length / 3;

                    for (int t = 0; t < triCount; t++)
                    {
                        int i0, i1, i2;
                        if (indices != null)
                        {
                            i0 = indices[t * 3]; i1 = indices[t * 3 + 1]; i2 = indices[t * 3 + 2];
                        }
                        else
                        {
                            i0 = t * 3; i1 = t * 3 + 1; i2 = t * 3 + 2;
                        }

                        var p0 = meshTransform * vertices[i0];
                        var p1 = meshTransform * vertices[i1];
                        var p2 = meshTransform * vertices[i2];

                        var hit = RayTriangleIntersect(from, direction, p0, p1, p2);
                        if (hit is not { } distance || distance >= bestDist) continue;

                        var hitPoint = from + direction * distance;
                        var (b0, b1, b2) = Barycentric(hitPoint, p0, p1, p2);
                        bestDist = distance;
                        bestUv = uvs[i0] * b0 + uvs[i1] * b1 + uvs[i2] * b2;
                        bestTarget = target;
                    }
                }
            }
        }

        return bestUv is { } uv && bestTarget is { } target2 ? (uv, target2) : null;
    }

    // Möller–Trumbore ray/triangle intersection — returns the ray-parameter
    // distance to the hit point, or null if the ray misses the triangle or
    // points away from it.
    private static float? RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float epsilon = 1e-7f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = dir.Cross(edge2);
        float a = edge1.Dot(h);
        if (a > -epsilon && a < epsilon) return null;

        float f = 1.0f / a;
        var s = origin - v0;
        float u = f * s.Dot(h);
        if (u < 0.0f || u > 1.0f) return null;

        var q = s.Cross(edge1);
        float v = f * dir.Dot(q);
        if (v < 0.0f || u + v > 1.0f) return null;

        float t = f * edge2.Dot(q);
        return t > epsilon ? t : null;
    }

    private static (float, float, float) Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var v0 = b - a; var v1 = c - a; var v2 = p - a;
        float d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1);
        float d20 = v2.Dot(v0), d21 = v2.Dot(v1);
        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-8f) return (1f, 0f, 0f);
        float v = (d11 * d20 - d01 * d21) / denom;
        float w = (d00 * d21 - d01 * d20) / denom;
        float u = 1f - v - w;
        return (u, v, w);
    }

    private static readonly Vector2I CanvasSize = new(1024, 1024);

    private Vector2 UvToCanvasPixels(Vector2 uv)
    {
        // Godot UV origin is top-left already for glTF-imported meshes, so
        // unlike the raylib-backed MAKO version this needs no vertical flip.
        // Every target's canvas is created at the same fixed resolution, so
        // this doesn't need to know which target it's being drawn into.
        return new Vector2(uv.X * CanvasSize.X, uv.Y * CanvasSize.Y);
    }

    private void DrawDot(Vector2 uv)
    {
        if (_activeTarget is not { } target) return;
        var dot = new Sprite2D
        {
            Texture = MakeCircleTexture(_brushSize, CurrentPaintColor()),
            Position = UvToCanvasPixels(uv),
        };
        target.Canvas2D.AddChild(dot);
    }

    private void DrawStrokeSegment(Vector2 fromUv, Vector2 toUv)
    {
        if (_activeTarget is not { } target) return;
        var line = new Line2D
        {
            Width = _brushSize,
            DefaultColor = CurrentPaintColor(),
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
        };
        line.AddPoint(UvToCanvasPixels(fromUv));
        line.AddPoint(UvToCanvasPixels(toUv));
        target.Canvas2D.AddChild(line);
    }

    private Color CurrentPaintColor()
    {
        var baseColor = _eraserEnabled ? new Color(0.9f, 0.9f, 0.86f) : _brushColor;
        baseColor.A = _eraserEnabled ? 1.0f : _brushOpacity;
        return baseColor;
    }

    // 2D shapes drag out a bounding box, live-previewed, and bake into the
    // canvas as an outlined Rectangle/Circle/Line node on release — mirroring
    // Paint 3D's rubber-band shape drawing.
    private void UpdateShape2DPreview()
    {
        if (_shape2DStart is not { } start || _shape2DEnd is not { } end) return;
        if (_activeTarget is not { } target) return;

        _shape2DPreviewNode?.QueueFree();
        _shape2DPreviewNode = BuildShape2DNode(start, end);
        target.Canvas2D.AddChild(_shape2DPreviewNode);
    }

    private void FinalizeShape2D()
    {
        _shape2DPreviewNode = null;
        _shape2DStart = null;
        _shape2DEnd = null;
        CommitHistoryEntry();
        _hintLabel.Text = "Shape added";
    }

    private Node2D BuildShape2DNode(Vector2 startUv, Vector2 endUv)
    {
        var start = UvToCanvasPixels(startUv);
        var end = UvToCanvasPixels(endUv);
        var color = CurrentPaintColor();

        switch (_shape2DType)
        {
            case Shape2DType.Line:
            {
                var line = new Line2D
                {
                    Width = _brushSize,
                    DefaultColor = color,
                    JointMode = Line2D.LineJointMode.Round,
                    BeginCapMode = Line2D.LineCapMode.Round,
                    EndCapMode = Line2D.LineCapMode.Round,
                };
                line.AddPoint(start);
                line.AddPoint(end);
                return line;
            }
            case Shape2DType.Circle:
            {
                float radius = start.DistanceTo(end);
                var polygon = new Line2D
                {
                    Width = _brushSize,
                    DefaultColor = color,
                    Closed = true,
                };
                const int segments = 32;
                for (int i = 0; i <= segments; i++)
                {
                    float angle = Mathf.Tau * i / segments;
                    polygon.AddPoint(start + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
                }
                return polygon;
            }
            default:
            {
                var rect = new Line2D
                {
                    Width = _brushSize,
                    DefaultColor = color,
                    Closed = true,
                };
                rect.AddPoint(start);
                rect.AddPoint(new Vector2(end.X, start.Y));
                rect.AddPoint(end);
                rect.AddPoint(new Vector2(start.X, end.Y));
                return rect;
            }
        }
    }

    private void ExportPaintTexture()
    {
        if (_activeTarget is not { } target) return;
        var image = target.CanvasViewport.GetTexture().GetImage();
        var path = "user://opencanvas3d-paint.png";
        var error = image.SavePng(path);
        _hintLabel.Text = error == Error.Ok
            ? $"Saved texture to {ProjectSettings.GlobalizePath(path)}"
            : $"Export failed: {error}";
    }

    private void HandleCameraOrbit()
    {
        if (Input.IsMouseButtonPressed(MouseButton.Right))
        {
            var mouse = GetViewport().GetMousePosition();
            if (_lastOrbitMouse is { } last)
            {
                var delta = mouse - last;
                _cameraYaw -= delta.X * 0.008f;
                _cameraPitch = Mathf.Clamp(_cameraPitch - delta.Y * 0.008f, -1.1f, 1.2f);
                UpdateCameraOrbit();
            }
            _lastOrbitMouse = mouse;
        }
        else
        {
            _lastOrbitMouse = null;
        }
    }

    private void UpdateCameraOrbit()
    {
        var offset = new Vector3(
            Mathf.Cos(_cameraPitch) * Mathf.Sin(_cameraYaw),
            Mathf.Sin(_cameraPitch),
            Mathf.Cos(_cameraPitch) * Mathf.Cos(_cameraYaw)
        ) * _cameraDistance;

        _camera.GlobalPosition = _cameraTarget + offset;
        _camera.LookAt(_cameraTarget, Vector3.Up);
    }

    // Imported models can be any size or origin offset (unlike the fixed
    // 2x2 default plane), so the camera needs to re-frame itself around the
    // model's actual bounds instead of assuming a fixed target/distance —
    // otherwise an imported model can render tiny, huge, or off-screen,
    // making it hard to even find, let alone paint on.
    private void FrameCameraOnModel()
    {
        Aabb? bounds = null;
        foreach (var meshInstance in _modelTarget?.MeshInstances ?? System.Linq.Enumerable.Empty<MeshInstance3D>())
        {
            var meshAabb = meshInstance.GlobalTransform * meshInstance.GetAabb();
            bounds = bounds is { } existing ? existing.Merge(meshAabb) : meshAabb;
        }

        if (bounds is not { } aabb || aabb.Size.LengthSquared() < 1e-6f)
        {
            _cameraTarget = new Vector3(0f, 0.45f, 0f);
            _cameraDistance = 4.7f;
            _minCameraDistance = 0.5f;
            _maxCameraDistance = 40.0f;
        }
        else
        {
            _cameraTarget = aabb.GetCenter();
            float radius = aabb.Size.Length() * 0.5f;
            _cameraDistance = radius / Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) * 1.3f;
            _minCameraDistance = Mathf.Max(0.05f, _cameraDistance * 0.15f);
            _maxCameraDistance = _cameraDistance * 10.0f;
        }
    }

    private static ImageTexture MakeCircleTexture(int diameter, Color color)
    {
        var image = Image.CreateEmpty(diameter, diameter, false, Image.Format.Rgba8);
        float radius = diameter / 2f;
        var center = new Vector2(radius, radius);
        for (int y = 0; y < diameter; y++)
            for (int x = 0; x < diameter; x++)
                if (new Vector2(x, y).DistanceTo(center) <= radius)
                    image.SetPixel(x, y, color);
        return ImageTexture.CreateFromImage(image);
    }
}
