using Godot;
using System;
using System.Linq;

namespace OpenCanvas3D;

// Extracted from PaintController.cs: everything that builds and updates the
// runtime UI (ribbon, side panel, widget factories). Purely a code-organization
// split — same class, same fields, no behavior change.
public partial class PaintController
{
    private const int SidePanelWidth = 260;

    // Rebuilding the whole toolbar on a theme change (rather than a granular
    // per-widget restyle pass) is simpler and safe here: every value the
    // ribbon/side panel reflect (active tool, brush settings, shape types,
    // 2D/3D view mode) already lives in persistent fields on this class, not
    // in the widgets themselves, so a full rebuild faithfully restores
    // current state — it just repaints with the new palette.
    private void RebuildToolbarForTheme()
    {
        BuildToolbar();
        UpdateSidePanelForTool();
        UpdateToolLabels();
        UpdateUndoRedoButtons();
    }

    private void BuildToolbar()
    {
        var ui = GetNode<CanvasLayer>("UI");

        // A rebuild (theme change) must remove whatever BuildToolbar created
        // last time before creating it again, or every toggle would stack a
        // duplicate title bar/ribbon/side panel on top — and it must happen
        // immediately (Free, not QueueFree) since AddChild below runs in
        // the same call and would otherwise silently get auto-renamed
        // ("TitleBar2") next to a same-named node still pending removal.
        foreach (var name in new[] { "TitleBar", "Ribbon", "OptionsRow", "SidePanel" })
            ui.GetNodeOrNull(name)?.Free();

        // --- Title bar: app name, mirroring Paint 3D's "Untitled - Paint 3D".
        var titleBar = new PanelContainer { Name = "TitleBar", CustomMinimumSize = new Vector2(0, 32) };
        titleBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        titleBar.OffsetBottom = 32;
        titleBar.AddThemeStyleboxOverride("panel", SolidStyleBox(_theme.Current.TitleBarBg));
        ui.AddChild(titleBar);
        var titleLabel = new Label
        {
            Text = "Untitled — OpenCanvas 3D",
            Modulate = _theme.Current.TextMuted,
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
        titleBarRight.AddChild(MakeActionButton(_theme.IsDark ? "Light" : "Dark", () =>
        {
            var settings = GetNode<AppSettings>("/root/AppSettings");
            settings.DarkTheme = !_theme.IsDark;
            settings.Save();
            _theme.SetDark(settings.DarkTheme);
        }));

        // --- Ribbon: a persistent tab strip (which group of tools is open)
        // over a content row that swaps per tab — Paint 3D's ribbon, rather
        // than one long flat row of every tool at once.
        _toolbar = new PanelContainer { Name = "Ribbon", CustomMinimumSize = new Vector2(0, 96) };
        _toolbar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _toolbar.OffsetTop = 32;
        _toolbar.OffsetBottom = 128;
        _toolbar.AddThemeStyleboxOverride("panel", SolidStyleBox(_theme.Current.RibbonBg));
        ui.AddChild(_toolbar);

        var ribbonColumn = new VBoxContainer();
        _toolbar.AddChild(ribbonColumn);

        var tabStripRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        tabStripRow.AddThemeConstantOverride("separation", 2);
        tabStripRow.OffsetLeft = 8;
        tabStripRow.OffsetTop = 2;
        tabStripRow.CustomMinimumSize = new Vector2(0, 28);
        ribbonColumn.AddChild(tabStripRow);

        tabStripRow.AddChild(MakeTabStripButton("Brushes", RibbonTab.Brushes));
        tabStripRow.AddChild(MakeTabStripButton("2D Shapes", RibbonTab.Shapes2D));
        tabStripRow.AddChild(MakeTabStripButton("3D Shapes", RibbonTab.Shapes3D));
        tabStripRow.AddChild(MakeTabStripButton("Text", RibbonTab.Text));
        tabStripRow.AddChild(MakeTabStripButton("Doodle", RibbonTab.Doodle));
        tabStripRow.AddChild(MakeTabStripButton("Canvas", RibbonTab.Canvas));
        tabStripRow.AddChild(MakeTabStripButton("File", RibbonTab.File));

        _ribbonContentRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        _ribbonContentRow.AddThemeConstantOverride("separation", 2);
        _ribbonContentRow.OffsetLeft = 8;
        _ribbonContentRow.CustomMinimumSize = new Vector2(0, 60);
        ribbonColumn.AddChild(_ribbonContentRow);

        BuildRibbonTabContent();
        UpdateTabStripHighlight();

        // --- Right side panel: brush grid, thickness/opacity, color + swatches.
        _sidePanel = new PanelContainer { Name = "SidePanel", CustomMinimumSize = new Vector2(SidePanelWidth, 0) };
        _sidePanel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _sidePanel.OffsetLeft = -SidePanelWidth;
        _sidePanel.OffsetTop = 128;
        _sidePanel.OffsetRight = 0;
        _sidePanel.OffsetBottom = 0;
        _sidePanel.AddThemeStyleboxOverride("panel", SolidStyleBox(_theme.Current.PanelBg));
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
            Modulate = _theme.Current.AccentBlue,
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
        _thicknessLabel = new Label { Text = $"{_brushSize}px", Modulate = _theme.Current.TextMuted };
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
        _opacityLabel = new Label { Text = "100%", Modulate = _theme.Current.TextMuted };
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

        _hintLabel.OffsetTop = 136;
        _hintLabel.OffsetBottom = 160;
        _hintLabel.OffsetLeft = 12;
        _hintLabel.OffsetRight = -SidePanelWidth - 16;
        _hintLabel.Modulate = _theme.Current.TextMuted;
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
            Modulate = _theme.Current.TextDark,
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
        button.AddThemeColorOverride("font_color", _theme.Current.TextDark);
        button.AddThemeColorOverride("font_hover_color", _theme.Current.TextDark);
        button.AddThemeColorOverride("font_pressed_color", _theme.Current.AccentBlue);
        button.AddThemeStyleboxOverride("hover", SolidStyleBox(_theme.Current.AccentBlueTint));
        button.AddThemeStyleboxOverride("pressed", SolidStyleBox(_theme.Current.AccentBlueTint));
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += action;
        return button;
    }

    // The tab strip is a persistent row of group selectors (Brushes, 2D
    // Shapes, ...); pressing one swaps _ribbonContentRow's children to that
    // group's tools and, where the group maps to exactly one tool (Text,
    // Doodle), activates it directly rather than requiring a second click.
    private Button MakeTabStripButton(string text, RibbonTab tab)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 24),
            Flat = true,
        };
        button.AddThemeFontSizeOverride("font_size", 12);
        _ribbonTabButtons[tab] = button;
        button.Pressed += () => SelectRibbonTab(tab);
        return button;
    }

    private void SelectRibbonTab(RibbonTab tab)
    {
        _activeRibbonTab = tab;
        switch (tab)
        {
            case RibbonTab.Text:
                SelectTool(Tool.Text);
                break;
            case RibbonTab.Doodle:
                SelectTool(Tool.Doodle);
                break;
            case RibbonTab.Shapes2D:
                SelectTool(Tool.Shape2D);
                break;
            case RibbonTab.Shapes3D:
                SelectTool(Tool.Shape3D);
                break;
            case RibbonTab.Brushes:
                if (_activeTool is not (Tool.Brush or Tool.Eraser or Tool.Fill or Tool.Eyedropper))
                    SelectTool(Tool.Brush);
                break;
        }
        BuildRibbonTabContent();
        UpdateTabStripHighlight();
    }

    private void UpdateTabStripHighlight()
    {
        foreach (var (tab, button) in _ribbonTabButtons)
        {
            bool active = tab == _activeRibbonTab;
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(active ? _theme.Current.AccentBlueTint : _theme.Current.RibbonBg));
            button.AddThemeColorOverride("font_color", active ? _theme.Current.AccentBlue : _theme.Current.TextDark);
        }
    }

    // Rebuilds only the content row for the active tab, leaving the tab
    // strip itself and the side panel alone — much cheaper than a full
    // BuildToolbar() rebuild for the common case of switching tabs.
    private void BuildRibbonTabContent()
    {
        foreach (Node child in _ribbonContentRow.GetChildren())
            child.Free();

        switch (_activeRibbonTab)
        {
            case RibbonTab.Brushes:
                _ribbonContentRow.AddChild(MakeRibbonTab("Brush", () => SelectTool(Tool.Brush)));
                _ribbonContentRow.AddChild(MakeRibbonTab("Eraser", () => SelectTool(Tool.Eraser)));
                _ribbonContentRow.AddChild(MakeRibbonTab("Fill", () => SelectTool(Tool.Fill)));
                _ribbonContentRow.AddChild(MakeRibbonTab("Eyedropper", () => SelectTool(Tool.Eyedropper)));
                break;
            case RibbonTab.Shapes2D:
                _ribbonContentRow.AddChild(new Label
                {
                    Text = "Pick a shape and drag on the canvas to draw it.",
                    Modulate = _theme.Current.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case RibbonTab.Shapes3D:
                _ribbonContentRow.AddChild(new Label
                {
                    Text = "Pick a shape and click in the scene to place it.",
                    Modulate = _theme.Current.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case RibbonTab.Text:
                _ribbonContentRow.AddChild(new Label
                {
                    Text = "Click on the canvas to type text.",
                    Modulate = _theme.Current.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case RibbonTab.Doodle:
                _ribbonContentRow.AddChild(new Label
                {
                    Text = "Drag in the 3D scene to draw a freehand tube mesh.",
                    Modulate = _theme.Current.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case RibbonTab.Canvas:
                _viewToggleButton = MakeActionButton(_is2DMode ? "2D view" : "3D view", ToggleViewMode);
                _ribbonContentRow.AddChild(_viewToggleButton);
                break;
            case RibbonTab.File:
                _ribbonContentRow.AddChild(MakeRibbonTab("Clear", () =>
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
                _ribbonContentRow.AddChild(MakeRibbonTab("Export", ExportPaintTexture));
                _ribbonContentRow.AddChild(MakeRibbonTab("Import\nModel", OpenImportDialog));
                break;
        }
    }

    private Button MakeBrushTile(BrushType type)
    {
        var button = new Button
        {
            Text = type.ToString(),
            CustomMinimumSize = new Vector2(48, 40),
        };
        button.AddThemeColorOverride("font_color", _theme.Current.TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(_theme.Current.TileBg));
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
        button.AddThemeColorOverride("font_color", _theme.Current.TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(_theme.Current.TileBg));
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
        button.AddThemeColorOverride("font_color", _theme.Current.TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(_theme.Current.TileBg));
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

        // Keep the ribbon tab in sync even when the tool changes through a
        // path that didn't go via SelectRibbonTab (e.g. the eyedropper or a
        // color-swatch click switching back to Brush) — the tab bar should
        // always reflect whichever tool is actually active.
        var tabForTool = tool switch
        {
            Tool.Text => RibbonTab.Text,
            Tool.Doodle => RibbonTab.Doodle,
            Tool.Shape2D => RibbonTab.Shapes2D,
            Tool.Shape3D => RibbonTab.Shapes3D,
            Tool.Brush or Tool.Eraser or Tool.Fill or Tool.Eyedropper => RibbonTab.Brushes,
            _ => _activeRibbonTab,
        };
        if (tabForTool != _activeRibbonTab)
        {
            _activeRibbonTab = tabForTool;
            if (_ribbonContentRow != null)
                BuildRibbonTabContent();
        }
        if (_ribbonTabButtons.Count > 0)
            UpdateTabStripHighlight();

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
        // The view-toggle button only exists while the Canvas tab is the
        // one currently shown in the ribbon's content row — it's rebuilt
        // (and the old instance freed) every time another tab is selected,
        // so the cached reference can be stale here.
        if (_activeRibbonTab == RibbonTab.Canvas && IsInstanceValid(_viewToggleButton))
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
        button.AddThemeColorOverride("font_color", _theme.Current.TextDark);
        button.AddThemeStyleboxOverride("normal", SolidStyleBox(_theme.Current.TileBg));
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
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _brushType ? _theme.Current.TileBgActive : _theme.Current.TileBg));
        foreach (var (type, button) in _shape2DButtons)
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _shape2DType ? _theme.Current.TileBgActive : _theme.Current.TileBg));
        foreach (var (type, button) in _shape3DButtons)
            button.AddThemeStyleboxOverride("normal", SolidStyleBox(type == _shape3DType ? _theme.Current.TileBgActive : _theme.Current.TileBg));

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
}
