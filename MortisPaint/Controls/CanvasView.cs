using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using Avalonia.Threading;
using MortisPaint.Editor;
using MortisPaint.Engine;
using SkiaSharp;

namespace MortisPaint.Controls;

/// <summary>
/// Surface de dessin : affiche la composition avec zoom / rotation / miroir de la vue,
/// et traduit souris, stylet (pression) et gestes tactiles en opérations du moteur.
/// </summary>
public sealed class CanvasView : Control, ICustomHitTest
{
    enum Gesture { None, Tool, Pan, Pinch, Pick, Blocked, Handle }
    enum HandleKind { SymmetryCenter, RulerCenter, RulerAngle, Text }
    enum TDrag { None, Move, Scale, ScaleX, ScaleY, Rotate }

    // Plan de travail sombre (famille Mortis) : la toile est posée dessus avec une ombre franche.
    static readonly IBrush DeskBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    static readonly IBrush SheetShadow = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
    static readonly IBrush SheetEdge = new SolidColorBrush(Color.FromArgb(0x9E, 0x52, 0x52, 0x52));
    static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 0xE5, 0x53, 0x3D)), 1.25, new DashStyle(new double[] { 6, 4 }, 0));
    static readonly IPen RulerPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0x4D, 0xD0, 0xDA)), 1, new DashStyle(new double[] { 3, 5 }, 0));
    static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)), 1.5);
    static readonly IPen RibbonPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x3D)), 1.5);
    static readonly IBrush HandleFill = Brushes.White;
    static readonly IPen CursorOuter = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.5);
    static readonly IPen CursorInner = new Pen(new SolidColorBrush(Color.FromArgb(220, 0x0C, 0x0C, 0x0C)), 0.75);
    const double DotStep = 22;
    static ImageBrush? _dotGrid;

    /// <summary>Trame pointillée du plan de travail, tuilée en un seul appel de dessin.</summary>
    static ImageBrush DotGrid
    {
        get
        {
            if (_dotGrid != null) return _dotGrid;
            const int scale = 2;
            int side = (int)DotStep * scale;
            var bmp = SkiaBitmaps.Create(side, side);
            SkiaBitmaps.Draw(bmp, c =>
            {
                c.Clear(SKColors.Transparent);
                using var p = new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x2A), IsAntialias = true };
                c.DrawCircle(side / 2f, side / 2f, 1.6f, p);
            });
            _dotGrid = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(0, 0, DotStep, DotStep, RelativeUnit.Absolute),
            };
            return _dotGrid;
        }
    }

    PaintEngine? _engine;
    EditorState? _state;
    WriteableBitmap? _display;
    bool _frameRequested;

    // Vue
    double _zoom = 1, _rotation;
    bool _flipView;
    Point _center;
    bool _fitPending = true;
    bool _isFitted;
    Size _lastSize;

    // Entrées
    Gesture _gesture;
    ToolKind _activeTool;
    int _toolPointerId = -1;
    readonly Dictionary<int, Point> _touches = new();
    readonly Dictionary<int, Point> _touchStarts = new();
    DateTime _touchDownTime;
    int _maxTouches;
    bool _touchMoved;
    Point _panStart, _centerStart;
    Point _pinchA, _pinchB;
    int _pinchIdA, _pinchIdB;
    double _pinchZoom, _pinchRot;
    SKPoint _pinchAnchor;
    DispatcherTimer? _longPress;
    bool _penPressureSeen;
    bool _shift;

    // Sessions d'outils
    BrushStrokeSession? _stroke;
    PixelBrushSession? _pixel;
    GradientSession? _gradient;
    BrushStrokeSession? _shapeSession;
    List<SKPoint>? _lasso;
    SKPoint _dragStart, _dragEnd;
    Geometry? _selectionGeometry;

    // Survol / pipette
    Point? _hover;
    Point _pickPos;
    SKColor? _pickRing;

    // Poignées (centre de symétrie, règle) et contrainte de la règle
    HandleKind _handle;
    SKPoint _handleOffset;
    Func<SKPoint, SKPoint>? _constraint;

    // Transformation
    SKPoint _tPivot, _tOffset, _tStartOffset;
    float _tScaleX = 1, _tScaleY = 1, _tRot, _tStartSX, _tStartSY, _tStartRot;
    TDrag _tDrag;
    Point _tStartPos;

    public CanvasView()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public event Action? ViewChanged;
    public event Action? CanvasPressed;
    public event Action? UndoGesture;
    public event Action? RedoGesture;
    public event Action<SKColor>? ColorPicked;
    public event Action<string>? Message;
    public event Action? TransformStateChanged;
    public event Action? BrushSizeChangedByWheel;

    public bool SpacePan { get; set; }
    public bool InputLocked { get; set; }

    /// <summary>Texte en cours de saisie (outil Texte) : il se déplace en glissant sur la toile.</summary>
    public TextSession? Text { get; set; }

    /// <summary>L'utilisateur a touché la toile avec l'outil Texte, sans texte en cours.</summary>
    public event Action<SKPoint>? TextRequested;
    /// <summary>La règle ou le centre de symétrie a été déplacé.</summary>
    public event Action? GuidesMoved;
    public TransformSession? Transform { get; private set; }

    public double Zoom => _zoom;
    public double DisplayZoomPercent => _zoom * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) * 100;
    public double RotationDegrees => _rotation * 180 / Math.PI;
    public bool IsViewFlipped => _flipView;

    public bool HitTest(Point point) => true;

    // ================================================================ cycle de vie

    public void Attach(PaintEngine engine, EditorState state)
    {
        Detach();
        _engine = engine;
        _state = state;
        engine.DisplayInvalidated += RequestFrame;
        engine.Selection.Changed += OnSelectionChanged;
        _display = SkiaBitmaps.Create(engine.Width, engine.Height);
        _fitPending = true;
        _flipView = false;
        _rotation = 0;
        OnSelectionChanged();
        engine.Invalidate(engine.Document.Bounds);
        UpdateCursor();
        RequestFrame();
        InvalidateArrange();
    }

    public void Detach()
    {
        if (_engine == null) return;
        EndToolGesture(commit: true);
        if (Transform != null) CommitTransform();
        _engine.DisplayInvalidated -= RequestFrame;
        _engine.Selection.Changed -= OnSelectionChanged;
        _engine = null;
        _display?.Dispose();
        _display = null;
    }

    /// <summary>À appeler quand l'outil change.</summary>
    public void OnToolChanged()
    {
        if (_state == null) return;
        if (_state.Tool != ToolKind.Transform && Transform != null) CommitTransform();
        UpdateCursor();
        InvalidateVisual();
    }

    void UpdateCursor()
    {
        var type = _state?.Tool switch
        {
            ToolKind.Hand => StandardCursorType.Hand,
            ToolKind.Transform => StandardCursorType.SizeAll,
            _ => StandardCursorType.Cross,
        };
        Cursor = new Cursor(type);
    }

    void OnSelectionChanged()
    {
        _selectionGeometry = null;
        var path = _engine?.Selection.Path;
        if (path != null)
        {
            try
            {
                _selectionGeometry = StreamGeometry.Parse(path.ToSvgPathData());
            }
            catch
            {
                var b = path.Bounds;
                _selectionGeometry = new RectangleGeometry(new Rect(b.Left, b.Top, b.Width, b.Height));
            }
        }
        InvalidateVisual();
    }

    // ================================================================ affichage

    void RequestFrame()
    {
        if (_frameRequested) return;
        _frameRequested = true;
        var top = TopLevel.GetTopLevel(this);
        if (top != null) top.RequestAnimationFrame(_ => UpdateDisplay());
        else Dispatcher.UIThread.Post(UpdateDisplay, DispatcherPriority.Render);
    }

    void UpdateDisplay()
    {
        _frameRequested = false;
        if (_engine == null || _display == null) return;
        if (_engine.UpdateComposite(out var rect))
        {
            bool paper = _engine.Document.PaperVisible;
            var composite = _engine.Composite;
            SkiaBitmaps.Draw(_display, c =>
            {
                c.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
                if (paper)
                {
                    using var p = new SKPaint { BlendMode = SKBlendMode.Src };
                    c.DrawBitmap(composite, 0, 0, p);
                }
                else
                {
                    using var p = new SKPaint { Shader = BitmapUtil.Checker };
                    c.DrawPaint(p);
                    c.DrawBitmap(composite, 0, 0);
                }
            });
        }
        InvalidateVisual();
    }

    Matrix ViewMatrix
    {
        get
        {
            if (_engine == null) return Matrix.Identity;
            return Matrix.CreateTranslation(-_engine.Width / 2.0, -_engine.Height / 2.0)
                   * Matrix.CreateScale(_flipView ? -_zoom : _zoom, _zoom)
                   * Matrix.CreateRotation(_rotation)
                   * Matrix.CreateTranslation(_center.X, _center.Y);
        }
    }

    public SKPoint ToCanvas(Point p)
    {
        var q = ViewMatrix.Invert().Transform(p);
        return new SKPoint((float)q.X, (float)q.Y);
    }

    Point ToScreen(SKPoint p) => ViewMatrix.Transform(new Point(p.X, p.Y));

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_fitPending || (_isFitted && finalSize != _lastSize))
        {
            Fit(finalSize);
        }
        else if (_lastSize.Width > 0 && finalSize != _lastSize)
        {
            _center += new Vector((finalSize.Width - _lastSize.Width) / 2, (finalSize.Height - _lastSize.Height) / 2);
        }
        _lastSize = finalSize;
        return base.ArrangeOverride(finalSize);
    }

    public void FitToView() => Fit(Bounds.Size);

    void Fit(Size size)
    {
        if (_engine == null || size.Width < 4 || size.Height < 4)
        {
            _fitPending = true;
            return;
        }
        _fitPending = false;
        _isFitted = true;
        double margin = Math.Min(28, Math.Min(size.Width, size.Height) * 0.05);
        _zoom = Math.Max(0.01, Math.Min((size.Width - margin * 2) / _engine.Width, (size.Height - margin * 2) / _engine.Height));
        _rotation = 0;
        _center = new Point(size.Width / 2, size.Height / 2);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    void PlaceCanvasPoint(SKPoint c, Point screen)
    {
        _center = new Point(0, 0);
        var q = ViewMatrix.Transform(new Point(c.X, c.Y));
        _center = new Point(screen.X - q.X, screen.Y - q.Y);
    }

    Point ScreenCenter => new(Bounds.Width / 2, Bounds.Height / 2);

    public void ZoomAt(Point screen, double factor)
    {
        if (_engine == null) return;
        _isFitted = false;
        var c = ToCanvas(screen);
        _zoom = Math.Clamp(_zoom * factor, 0.02, 64);
        PlaceCanvasPoint(c, screen);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAt(ScreenCenter, factor);

    public void ZoomActualPixels()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        ZoomAt(ScreenCenter, 1 / scaling / _zoom);
    }

    public void FlipView()
    {
        if (_engine == null) return;
        _isFitted = false;
        var c = ToCanvas(ScreenCenter);
        _flipView = !_flipView;
        PlaceCanvasPoint(c, ScreenCenter);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void RotateView(double degrees, Point? around = null)
    {
        if (_engine == null) return;
        _isFitted = false;
        var pivot = around ?? ScreenCenter;
        var c = ToCanvas(pivot);
        _rotation += degrees * Math.PI / 180;
        PlaceCanvasPoint(c, pivot);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void ResetRotation()
    {
        if (_engine == null) return;
        var c = ToCanvas(ScreenCenter);
        _rotation = 0;
        PlaceCanvasPoint(c, ScreenCenter);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(DeskBrush, new Rect(Bounds.Size));
        ctx.FillRectangle(DotGrid, new Rect(Bounds.Size));
        if (_engine == null || _display == null) return;

        var m = ViewMatrix;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double physical = _zoom * scaling;
        var mode = physical >= 2.0 ? BitmapInterpolationMode.None
            : physical < 1.0 ? BitmapInterpolationMode.MediumQuality
            : BitmapInterpolationMode.LowQuality;

        using (ctx.PushTransform(m))
        {
            var rect = new Rect(0, 0, _engine.Width, _engine.Height);
            double px = 1 / _zoom;
            ctx.FillRectangle(SheetShadow, rect.Translate(new Vector(3 * px, 3 * px)));
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                ctx.DrawImage(_display, rect);
            ctx.DrawRectangle(new Pen(SheetEdge, px), rect);
        }
        DrawOverlays(ctx, m);
    }

    void DrawOverlays(DrawingContext ctx, Matrix m)
    {
        if (_engine == null || _state == null) return;
        var sym = _engine.Symmetry;
        if (sym.IsActive)
        {
            foreach (var (a, b) in sym.GetGuides(_engine.Width, _engine.Height))
                ctx.DrawLine(GuidePen, ToScreen(a), ToScreen(b));
            ctx.DrawEllipse(HandleFill, RibbonPen, ToScreen(new SKPoint(sym.CenterX, sym.CenterY)), 7, 7);
        }
        var ruler = _engine.Ruler;
        if (ruler.IsActive)
        {
            using (ctx.PushClip(new Rect(Bounds.Size)))
                foreach (var (a, b) in ruler.GetGuides(_engine.Width, _engine.Height))
                    ctx.DrawLine(RulerPen, ToScreen(a), ToScreen(b));
            var c = ToScreen(ruler.Center);
            if (ruler.Mode == RulerMode.Concentric)
            {
                foreach (float rr in new[] { 0.1f, 0.2f, 0.32f, 0.46f })
                {
                    double rad = rr * Math.Max(_engine.Width, _engine.Height) * _zoom;
                    ctx.DrawEllipse(null, RulerPen, c, rad, rad);
                }
            }
            ctx.DrawEllipse(HandleFill, HandlePen, c, 8, 8);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0xD0, 0xDA)), 2), c, 11, 11);
            if (ruler.Mode == RulerMode.Parallel)
            {
                var knob = RulerKnob();
                ctx.DrawLine(HandlePen, c, knob);
                ctx.DrawEllipse(HandleFill, HandlePen, knob, 6, 6);
            }
        }

        double thin = 1.5 / _zoom;
        var antsWhite = new Pen(Brushes.White, thin);
        var antsBlack = new Pen(Brushes.Black, thin, new DashStyle(new double[] { 3, 3 }, 0));

        if (_selectionGeometry != null && Transform == null)
        {
            using (ctx.PushTransform(m))
            {
                ctx.DrawGeometry(null, antsWhite, _selectionGeometry);
                ctx.DrawGeometry(null, antsBlack, _selectionGeometry);
            }
        }

        if (_lasso != null && _gesture == Gesture.Tool && _activeTool == ToolKind.Select)
        {
            var g = BuildSelectionPreview();
            if (g != null)
            {
                using (ctx.PushTransform(m))
                {
                    ctx.DrawGeometry(null, antsWhite, g);
                    ctx.DrawGeometry(null, antsBlack, g);
                }
            }
        }

        if (_gradient != null)
        {
            var a = ToScreen(_dragStart);
            var b = ToScreen(_dragEnd);
            ctx.DrawLine(new Pen(Brushes.White, 2), a, b);
            ctx.DrawLine(new Pen(Brushes.Black, 1), a, b);
            ctx.DrawEllipse(HandleFill, HandlePen, a, 6, 6);
            ctx.DrawEllipse(HandleFill, HandlePen, b, 6, 6);
        }

        if (Transform != null) DrawTransformHandles(ctx);

        if (_hover is { } hp && _gesture is Gesture.None or Gesture.Tool && Transform == null)
        {
            var tool = _state.Tool;
            if (EditorState.IsBrushTool(tool))
            {
                double r = _state.CurrentBrush.Size / 2 * _zoom;
                if (r >= 3)
                {
                    ctx.DrawEllipse(null, CursorOuter, hp, r, r);
                    ctx.DrawEllipse(null, CursorInner, hp, r - 1, r - 1);
                }
            }
        }

        if (_pickRing is { } pr && _gesture == Gesture.Pick)
        {
            var brush = new SolidColorBrush(pr.ToAvalonia());
            ctx.DrawEllipse(null, new Pen(Brushes.White, 14), _pickPos + new Vector(0, -70), 30, 30);
            ctx.DrawEllipse(null, new Pen(brush, 12), _pickPos + new Vector(0, -70), 30, 30);
            ctx.DrawEllipse(null, CursorInner, _pickPos, 4, 4);
        }
    }

    Geometry? BuildSelectionPreview()
    {
        if (_state == null || _lasso == null) return null;
        if (_state.SelectKind == SelectKind.Lasso)
        {
            if (_lasso.Count < 2) return null;
            return new PolylineGeometry(_lasso.Select(p => new Point(p.X, p.Y)), false);
        }
        var r = NormalizedRect(_dragStart, _dragEnd);
        var rect = new Rect(r.Left, r.Top, r.Width, r.Height);
        return _state.SelectKind == SelectKind.Ellipse ? new EllipseGeometry(rect) : new RectangleGeometry(rect);
    }

    // ================================================================ pointeurs

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_engine == null || _state == null || InputLocked) return;
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        e.Handled = true;
        _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        CanvasPressed?.Invoke();

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches[e.Pointer.Id] = pos;
            _touchStarts[e.Pointer.Id] = pos;
            e.Pointer.Capture(this);
            if (_touches.Count == 1)
            {
                _touchDownTime = DateTime.UtcNow;
                _maxTouches = 1;
                _touchMoved = false;
                bool draws = _state.TouchDraws && _state.Tool != ToolKind.Hand;
                if (draws)
                {
                    BeginTool(e.Pointer.Id, pos, 1f, false);
                    StartLongPress();
                }
                else
                {
                    BeginPan(pos);
                }
            }
            else
            {
                _maxTouches = Math.Max(_maxTouches, _touches.Count);
                StopLongPress();
                if (_gesture is Gesture.Tool or Gesture.Pick) EndToolGesture(commit: false);
                if (_touches.Count == 2 || _gesture != Gesture.Pinch) BeginPinch();
            }
            return;
        }

        e.Pointer.Capture(this);
        _hover = pos;
        if (pt.Properties.IsMiddleButtonPressed || SpacePan || _state.Tool == ToolKind.Hand)
        {
            BeginPan(pos);
            return;
        }
        if (pt.Properties.IsLeftButtonPressed && TryBeginHandle(e.Pointer.Id, pos)) return;
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsBarrelButtonPressed)
        {
            _gesture = Gesture.Pick;
            _toolPointerId = e.Pointer.Id;
            Pick(pos);
            return;
        }
        BeginTool(e.Pointer.Id, pos, PressureOf(e.Pointer.Type, pt.Properties), pt.Properties.IsEraser);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_engine == null || _state == null) return;
        var pos = e.GetPosition(this);
        _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Pointer.Type == PointerType.Touch)
        {
            if (!_touches.ContainsKey(e.Pointer.Id)) return;
            _touches[e.Pointer.Id] = pos;
            if (_touchStarts.TryGetValue(e.Pointer.Id, out var start) && Dist(start, pos) > 12)
            {
                _touchMoved = true;
                StopLongPress();
            }
        }
        else
        {
            _hover = pos;
        }

        switch (_gesture)
        {
            case Gesture.Pan:
                _center = _centerStart + (pos - _panStart);
                ViewChanged?.Invoke();
                InvalidateVisual();
                break;
            case Gesture.Pinch:
                UpdatePinch();
                break;
            case Gesture.Tool:
                if (e.Pointer.Id == _toolPointerId) MoveTool(e, pos);
                break;
            case Gesture.Pick:
                if (e.Pointer.Id == _toolPointerId) Pick(pos);
                break;
            case Gesture.Handle:
                if (e.Pointer.Id == _toolPointerId) MoveHandle(pos);
                break;
            default:
                if (e.Pointer.Type != PointerType.Touch && EditorState.IsBrushTool(_state.Tool)) InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_engine == null || _state == null) return;

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches.Remove(e.Pointer.Id);
            _touchStarts.Remove(e.Pointer.Id);
            if (_gesture == Gesture.Tool && e.Pointer.Id == _toolPointerId) EndToolGesture(commit: true);
            else if (_gesture == Gesture.Handle && e.Pointer.Id == _toolPointerId) _gesture = Gesture.None;
            else if (_gesture == Gesture.Pick && e.Pointer.Id == _toolPointerId) EndPick();
            else if (_gesture == Gesture.Pinch && _touches.Count < 2) _gesture = Gesture.Blocked;

            if (_touches.Count == 0)
            {
                bool quick = (DateTime.UtcNow - _touchDownTime).TotalMilliseconds < 400;
                if (_maxTouches >= 2 && !_touchMoved && quick && _state.TwoFingerUndo)
                {
                    if (_maxTouches == 2) UndoGesture?.Invoke();
                    else RedoGesture?.Invoke();
                }
                _gesture = Gesture.None;
                _maxTouches = 0;
            }
            e.Pointer.Capture(null);
            return;
        }

        switch (_gesture)
        {
            case Gesture.Tool: EndToolGesture(commit: true); break;
            case Gesture.Pick: EndPick(); break;
            default: _gesture = Gesture.None; break;
        }
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer.Type == PointerType.Touch)
        {
            if (!_touches.Remove(e.Pointer.Id)) return;
            _touchStarts.Remove(e.Pointer.Id);
            if (_gesture == Gesture.Tool && e.Pointer.Id == _toolPointerId) EndToolGesture(commit: true);
            if (_touches.Count == 0) _gesture = Gesture.None;
            return;
        }
        if (_gesture == Gesture.Tool && e.Pointer.Id == _toolPointerId) EndToolGesture(commit: true);
        else if (_gesture != Gesture.None) _gesture = Gesture.None;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_engine == null || _state == null || InputLocked) return;
        var pos = e.GetPosition(this);
        double d = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var b = _state.CurrentBrush;
            b.Size = (float)Math.Clamp(b.Size * Math.Pow(1.12, d), 1, b.MaxSize);
            _state.NotifyBrushEdited();
            BrushSizeChangedByWheel?.Invoke();
            InvalidateVisual();
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            RotateView(d * 7.5, pos);
        }
        else
        {
            ZoomAt(pos, Math.Pow(1.18, d));
        }
        e.Handled = true;
    }

    float PressureOf(PointerType type, PointerPointProperties props)
    {
        if (type != PointerType.Pen) return 1f;
        float p = props.Pressure;
        if (p > 0f && p < 0.999f) _penPressureSeen = true;
        if (!_penPressureSeen) return 1f;
        return Math.Clamp(p, 0.01f, 1f);
    }

    // ================================================================ navigation

    void BeginPan(Point pos)
    {
        _isFitted = false;
        _gesture = Gesture.Pan;
        _panStart = pos;
        _centerStart = _center;
    }

    void BeginPinch()
    {
        _isFitted = false;
        if (_touches.Count < 2) return;
        var ids = _touches.Keys.Take(2).ToArray();
        _pinchIdA = ids[0];
        _pinchIdB = ids[1];
        _pinchA = _touches[_pinchIdA];
        _pinchB = _touches[_pinchIdB];
        _pinchZoom = _zoom;
        _pinchRot = _rotation;
        _pinchAnchor = ToCanvas(Mid(_pinchA, _pinchB));
        _gesture = Gesture.Pinch;
    }

    void UpdatePinch()
    {
        if (!_touches.TryGetValue(_pinchIdA, out var a) || !_touches.TryGetValue(_pinchIdB, out var b)) return;
        double d0 = Dist(_pinchA, _pinchB), d1 = Dist(a, b);
        if (d0 < 1) return;
        _zoom = Math.Clamp(_pinchZoom * d1 / d0, 0.02, 64);
        double ang0 = Math.Atan2(_pinchB.Y - _pinchA.Y, _pinchB.X - _pinchA.X);
        double ang1 = Math.Atan2(b.Y - a.Y, b.X - a.X);
        double rot = _pinchRot + (ang1 - ang0);
        double deg = rot * 180 / Math.PI;
        double snapped = Math.Round(deg / 90) * 90;
        if (Math.Abs(deg - snapped) < 4) rot = snapped * Math.PI / 180;
        _rotation = rot;
        PlaceCanvasPoint(_pinchAnchor, Mid(a, b));
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    void StartLongPress()
    {
        if (_state == null || !(_state.Tool is ToolKind.Brush or ToolKind.Eraser or ToolKind.Smudge or ToolKind.Blur)) return;
        if (_longPress == null)
        {
            _longPress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _longPress.Tick += (_, _) => OnLongPress();
        }
        _longPress.Stop();
        _longPress.Start();
    }

    void StopLongPress() => _longPress?.Stop();

    void OnLongPress()
    {
        StopLongPress();
        if (_gesture != Gesture.Tool || _touches.Count != 1 || _touchMoved) return;
        int id = _toolPointerId;
        if (!_touches.TryGetValue(id, out var pos)) return;
        EndToolGesture(commit: false);
        _gesture = Gesture.Pick;
        _toolPointerId = id;
        Pick(pos);
    }

    // ================================================================ outils

    /// <summary>Saisit une poignée (centre de symétrie, règle, texte) si le pointeur est dessus.</summary>
    bool TryBeginHandle(int pointerId, Point pos)
    {
        if (_engine == null || _state == null) return false;
        const double hit = 22;
        var cp = ToCanvas(pos);
        void Start(HandleKind kind, SKPoint anchor)
        {
            _handle = kind;
            _handleOffset = new SKPoint(anchor.X - cp.X, anchor.Y - cp.Y);
            _gesture = Gesture.Handle;
            _toolPointerId = pointerId;
        }
        var ruler = _engine.Ruler;
        if (ruler.IsActive)
        {
            if (ruler.Mode == RulerMode.Parallel && Dist(pos, RulerKnob()) < hit) { Start(HandleKind.RulerAngle, ruler.Center); return true; }
            if (Dist(pos, ToScreen(ruler.Center)) < hit) { Start(HandleKind.RulerCenter, ruler.Center); return true; }
        }
        var sym = _engine.Symmetry;
        if (sym.IsActive && Dist(pos, ToScreen(new SKPoint(sym.CenterX, sym.CenterY))) < hit)
        {
            Start(HandleKind.SymmetryCenter, new SKPoint(sym.CenterX, sym.CenterY));
            return true;
        }
        if (Text != null && _state.Tool == ToolKind.Text)
        {
            Start(HandleKind.Text, Text.Origin);
            return true;
        }
        return false;
    }

    Point RulerKnob()
    {
        var r = _engine!.Ruler;
        var c = ToScreen(r.Center);
        double a = r.AngleDegrees * Math.PI / 180;
        var tip = ToScreen(new SKPoint(r.CenterX + (float)Math.Cos(a) * 100, r.CenterY + (float)Math.Sin(a) * 100));
        var dir = tip - c;
        double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1e-3) return c + new Vector(70, 0);
        return c + dir / len * 70;
    }

    void MoveHandle(Point pos)
    {
        if (_engine == null) return;
        var cp = ToCanvas(pos);
        var p = new SKPoint(cp.X + _handleOffset.X, cp.Y + _handleOffset.Y);
        switch (_handle)
        {
            case HandleKind.SymmetryCenter:
                _engine.Symmetry.CenterX = p.X;
                _engine.Symmetry.CenterY = p.Y;
                break;
            case HandleKind.RulerCenter:
                _engine.Ruler.CenterX = p.X;
                _engine.Ruler.CenterY = p.Y;
                break;
            case HandleKind.RulerAngle:
            {
                var r = _engine.Ruler;
                float deg = MathF.Atan2(cp.Y - r.CenterY, cp.X - r.CenterX) * 180f / MathF.PI;
                if (_shift) deg = MathF.Round(deg / 15f) * 15f;
                r.AngleDegrees = deg;
                break;
            }
            case HandleKind.Text:
                Text?.Update(Text.Style, p);
                break;
        }
        GuidesMoved?.Invoke();
        InvalidateVisual();
    }

    SKPoint Constrain(SKPoint p) => _constraint?.Invoke(p) ?? p;

    void BeginTool(int pointerId, Point pos, float pressure, bool penEraser)
    {
        var engine = _engine!;
        var state = _state!;
        if (TryBeginHandle(pointerId, pos)) return;
        var cp = ToCanvas(pos);
        _constraint = null;
        var tool0 = penEraser ? ToolKind.Eraser : state.Tool;
        if (engine.Ruler.IsActive && tool0 is ToolKind.Brush or ToolKind.Eraser or ToolKind.Smudge or ToolKind.Blur)
        {
            _constraint = engine.Ruler.Begin(cp, (float)(6 / _zoom));
            cp = Constrain(cp);
        }
        _toolPointerId = pointerId;
        _dragStart = _dragEnd = cp;
        var tool = penEraser ? ToolKind.Eraser : state.Tool;
        _activeTool = tool;

        bool paints = tool is ToolKind.Brush or ToolKind.Eraser or ToolKind.Smudge or ToolKind.Blur
            or ToolKind.Fill or ToolKind.Gradient or ToolKind.Shape;
        if (paints && !engine.ActiveLayer.Visible)
        {
            Message?.Invoke("Le calque actif est masqué");
            _gesture = Gesture.Blocked;
            return;
        }

        switch (tool)
        {
            case ToolKind.Brush:
            case ToolKind.Eraser:
                var brush = tool == ToolKind.Eraser ? state.EraserBrush : state.PaintBrush;
                _stroke = new BrushStrokeSession(engine, brush, state.Color,
                    tool == ToolKind.Eraser ? StrokeMode.Erase : StrokeMode.Paint, (float)_zoom);
                _stroke.Add(new InputPoint(cp.X, cp.Y, pressure));
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Smudge:
            case ToolKind.Blur:
                _pixel = new PixelBrushSession(engine, state.CurrentBrush,
                    tool == ToolKind.Smudge ? PixelBrushMode.Smudge : PixelBrushMode.Blur);
                _pixel.Add(new InputPoint(cp.X, cp.Y, pressure));
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Fill:
                using (var tone = state.CreateTone())
                    engine.Fill((int)MathF.Floor(cp.X), (int)MathF.Floor(cp.Y), state.Color, state.FillTolerance,
                        state.FillExpand, state.FillSampleAll, state.FillOpacity, tone);
                state.PushRecent(state.Color);
                _gesture = Gesture.Blocked;
                break;
            case ToolKind.Gradient:
                var to = state.GradientToTransparent ? state.Color.WithAlpha(0) : state.SecondaryColor;
                _gradient = new GradientSession(engine, cp, state.Color, to, state.GradientKind, state.FillOpacity);
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Eyedropper:
                _gesture = Gesture.Pick;
                Pick(pos);
                break;
            case ToolKind.Shape:
                _shapeSession = new BrushStrokeSession(engine, state.PaintBrush, state.Color, StrokeMode.Paint, (float)_zoom);
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Text:
                TextRequested?.Invoke(cp);
                _gesture = Gesture.Blocked;
                break;
            case ToolKind.Select when state.SelectKind == SelectKind.Wand:
                if (!engine.SelectByColor((int)MathF.Floor(cp.X), (int)MathF.Floor(cp.Y), state.FillTolerance, state.FillSampleAll, CurrentSelectOp()))
                    Message?.Invoke("Rien à sélectionner ici");
                _gesture = Gesture.Blocked;
                break;
            case ToolKind.Select:
                _lasso = new List<SKPoint> { cp };
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Transform:
                if (Transform == null && !BeginTransform())
                {
                    _gesture = Gesture.Blocked;
                    return;
                }
                BeginTransformDrag(pos);
                _gesture = Gesture.Tool;
                break;
            case ToolKind.Hand:
                BeginPan(pos);
                break;
        }
    }

    void MoveTool(PointerEventArgs e, Point pos)
    {
        var cp = ToCanvas(pos);
        switch (_activeTool)
        {
            case ToolKind.Brush:
            case ToolKind.Eraser:
            case ToolKind.Smudge:
            case ToolKind.Blur:
                foreach (var ip in e.GetIntermediatePoints(this))
                {
                    var c = Constrain(ToCanvas(ip.Position));
                    var input = new InputPoint(c.X, c.Y, PressureOf(e.Pointer.Type, ip.Properties));
                    _stroke?.Add(input);
                    _pixel?.Add(input);
                }
                break;
            case ToolKind.Gradient:
                _dragEnd = _shift ? Snap45(_dragStart, cp) : cp;
                _gradient?.Update(_dragEnd);
                InvalidateVisual();
                break;
            case ToolKind.Shape:
                _dragEnd = cp;
                UpdateShape();
                break;
            case ToolKind.Select:
                if (_lasso == null) break;
                _dragEnd = cp;
                if (_state!.SelectKind == SelectKind.Lasso)
                {
                    var last = _lasso[^1];
                    if (SKPoint.Distance(last, cp) * _zoom > 2) _lasso.Add(cp);
                }
                else if (_shift)
                {
                    float s = MathF.Max(MathF.Abs(cp.X - _dragStart.X), MathF.Abs(cp.Y - _dragStart.Y));
                    _dragEnd = new SKPoint(_dragStart.X + MathF.Sign(cp.X - _dragStart.X) * s, _dragStart.Y + MathF.Sign(cp.Y - _dragStart.Y) * s);
                }
                InvalidateVisual();
                break;
            case ToolKind.Transform:
                UpdateTransformDrag(pos);
                break;
        }
    }

    void EndToolGesture(bool commit)
    {
        StopLongPress();
        if (_stroke != null)
        {
            if (commit)
            {
                _stroke.End();
                if (_activeTool == ToolKind.Brush) _state?.PushRecent(_state.Color);
            }
            else _stroke.Cancel();
            _stroke = null;
        }
        if (_pixel != null)
        {
            if (commit) _pixel.End();
            else _pixel.Cancel();
            _pixel = null;
        }
        if (_gradient != null)
        {
            if (commit) _gradient.End();
            else _gradient.Cancel();
            _gradient = null;
        }
        if (_shapeSession != null)
        {
            if (commit)
            {
                _shapeSession.End();
                _state?.PushRecent(_state.Color);
            }
            else _shapeSession.Cancel();
            _shapeSession = null;
        }
        if (_lasso != null)
        {
            if (commit) FinishSelection();
            _lasso = null;
        }
        _tDrag = TDrag.None;
        _constraint = null;
        _gesture = Gesture.None;
        _toolPointerId = -1;
        InvalidateVisual();
    }

    void Pick(Point pos)
    {
        if (_engine == null || _state == null) return;
        var c = ToCanvas(pos);
        var col = _engine.PickColor((int)MathF.Floor(c.X), (int)MathF.Floor(c.Y), _state.PickSampleAll);
        _pickPos = pos;
        if (col is { } cc)
        {
            _state.SetColor(cc);
            _pickRing = cc;
            ColorPicked?.Invoke(cc);
        }
        InvalidateVisual();
    }

    void EndPick()
    {
        _gesture = Gesture.None;
        _pickRing = null;
        _toolPointerId = -1;
        InvalidateVisual();
    }

    bool _alt;

    SelectionOp CurrentSelectOp() => _shift ? SelectionOp.Add : _alt ? SelectionOp.Subtract : _state!.SelectOp;

    void FinishSelection()
    {
        if (_engine == null || _state == null || _lasso == null) return;
        var path = new SKPath();
        if (_state.SelectKind == SelectKind.Lasso)
        {
            if (_lasso.Count < 3)
            {
                if (CurrentSelectOp() == SelectionOp.Replace) _engine.Selection.Clear();
                path.Dispose();
                return;
            }
            path.MoveTo(_lasso[0]);
            for (int i = 1; i < _lasso.Count; i++) path.LineTo(_lasso[i]);
            path.Close();
        }
        else
        {
            var r = NormalizedRect(_dragStart, _dragEnd);
            if (r.Width * _zoom < 3 && r.Height * _zoom < 3)
            {
                if (CurrentSelectOp() == SelectionOp.Replace) _engine.Selection.Clear();
                path.Dispose();
                return;
            }
            if (_state.SelectKind == SelectKind.Ellipse) path.AddOval(r);
            else path.AddRect(r);
        }
        _engine.Selection.Combine(path, CurrentSelectOp());
    }

    void UpdateShape()
    {
        if (_shapeSession == null || _state == null) return;
        var a = _dragStart;
        var b = _dragEnd;
        var pts = new List<SKPoint>();
        SKPath? fill = null;
        switch (_state.Shape)
        {
            case ShapeKind.Line:
                if (_shift) b = Snap45(a, b);
                pts = Densify(new[] { a, b }, false);
                break;
            case ShapeKind.Rectangle:
            {
                var r = ShapeRect(a, b);
                if (_state.ShapeFilled)
                {
                    fill = new SKPath();
                    fill.AddRect(r);
                }
                else
                {
                    pts = Densify(new[] { new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top), new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom) }, true);
                }
                break;
            }
            case ShapeKind.Ellipse:
            {
                var r = ShapeRect(a, b);
                if (_state.ShapeFilled)
                {
                    fill = new SKPath();
                    fill.AddOval(r);
                }
                else
                {
                    int n = Math.Clamp((int)((r.Width + r.Height) * _zoom / 4), 24, 360);
                    var ring = new SKPoint[n];
                    for (int i = 0; i < n; i++)
                    {
                        double t = 2 * Math.PI * i / n;
                        ring[i] = new SKPoint(r.MidX + (float)Math.Cos(t) * r.Width / 2, r.MidY + (float)Math.Sin(t) * r.Height / 2);
                    }
                    pts = Densify(ring, true);
                }
                break;
            }
        }
        _shapeSession.SetShape(pts, false, fill);
        fill?.Dispose();
    }

    SKRect ShapeRect(SKPoint a, SKPoint b)
    {
        if (_shift)
        {
            float s = MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
            b = new SKPoint(a.X + MathF.Sign(b.X - a.X) * s, a.Y + MathF.Sign(b.Y - a.Y) * s);
        }
        return NormalizedRect(a, b);
    }

    static SKRect NormalizedRect(SKPoint a, SKPoint b) =>
        new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));

    static SKPoint Snap45(SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return new SKPoint(a.X + (float)Math.Cos(ang) * len, a.Y + (float)Math.Sin(ang) * len);
    }

    /// <summary>Insère des points intermédiaires pour garder des angles nets malgré le lissage du trait.</summary>
    static List<SKPoint> Densify(IReadOnlyList<SKPoint> pts, bool closed)
    {
        var list = new List<SKPoint>();
        int count = closed ? pts.Count : pts.Count - 1;
        if (pts.Count == 0) return list;
        list.Add(pts[0]);
        for (int i = 0; i < count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            float len = SKPoint.Distance(a, b);
            int n = Math.Max(1, (int)(len / 2f));
            for (int k = 1; k <= n; k++)
            {
                float t = k / (float)n;
                list.Add(new SKPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return list;
    }

    // ================================================================ transformation

    public bool BeginTransform()
    {
        if (Transform != null) return true;
        if (_engine == null) return false;
        if (!_engine.ActiveLayer.Visible)
        {
            Message?.Invoke("Le calque actif est masqué");
            return false;
        }
        var s = new TransformSession(_engine);
        if (s.IsEmpty)
        {
            s.Cancel();
            Message?.Invoke("Rien à transformer sur ce calque");
            return false;
        }
        Transform = s;
        var b = s.ContentBounds;
        _tPivot = new SKPoint(b.MidX, b.MidY);
        _tOffset = default;
        _tScaleX = _tScaleY = 1;
        _tRot = 0;
        TransformStateChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    SKMatrix BuildTransformMatrix() =>
        SKMatrix.CreateTranslation(_tPivot.X + _tOffset.X, _tPivot.Y + _tOffset.Y)
            .PreConcat(SKMatrix.CreateRotationDegrees(_tRot))
            .PreConcat(SKMatrix.CreateScale(_tScaleX, _tScaleY))
            .PreConcat(SKMatrix.CreateTranslation(-_tPivot.X, -_tPivot.Y));

    void ApplyTransform()
    {
        Transform?.SetMatrix(BuildTransformMatrix());
        InvalidateVisual();
    }

    public void CommitTransform()
    {
        if (Transform == null) return;
        Transform.Commit();
        Transform = null;
        _tDrag = TDrag.None;
        TransformStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void CancelTransform()
    {
        if (Transform == null) return;
        Transform.Cancel();
        Transform = null;
        _tDrag = TDrag.None;
        TransformStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void TransformFlip(bool horizontal)
    {
        if (Transform == null && !BeginTransform()) return;
        if (horizontal) _tScaleX = -_tScaleX;
        else _tScaleY = -_tScaleY;
        ApplyTransform();
    }

    public void TransformRotate(float degrees)
    {
        if (Transform == null && !BeginTransform()) return;
        _tRot += degrees;
        ApplyTransform();
    }

    public void TransformReset()
    {
        if (Transform == null) return;
        _tOffset = default;
        _tScaleX = _tScaleY = 1;
        _tRot = 0;
        ApplyTransform();
    }

    Point[] TransformCorners()
    {
        var m = BuildTransformMatrix();
        var b = Transform!.ContentBounds;
        return new[]
        {
            ToScreen(m.MapPoint(b.Left, b.Top)), ToScreen(m.MapPoint(b.Right, b.Top)),
            ToScreen(m.MapPoint(b.Right, b.Bottom)), ToScreen(m.MapPoint(b.Left, b.Bottom)),
        };
    }

    static Point RotateHandle(Point[] c)
    {
        var topMid = Mid(c[0], c[1]);
        var center = Mid(c[0], c[2]);
        Vector dir = topMid - center;
        double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1e-3) dir = new Vector(0, -1);
        else dir /= len;
        return topMid + dir * 40;
    }

    void BeginTransformDrag(Point pos)
    {
        var c = TransformCorners();
        _tStartPos = pos;
        _tStartOffset = _tOffset;
        _tStartSX = _tScaleX;
        _tStartSY = _tScaleY;
        _tStartRot = _tRot;
        const double hit = 26;
        if (Dist(pos, RotateHandle(c)) < hit) { _tDrag = TDrag.Rotate; return; }
        if (c.Any(p => Dist(p, pos) < hit)) { _tDrag = TDrag.Scale; return; }
        if (Dist(pos, Mid(c[0], c[1])) < hit || Dist(pos, Mid(c[2], c[3])) < hit) { _tDrag = TDrag.ScaleY; return; }
        if (Dist(pos, Mid(c[1], c[2])) < hit || Dist(pos, Mid(c[3], c[0])) < hit) { _tDrag = TDrag.ScaleX; return; }
        _tDrag = TDrag.Move;
    }

    void UpdateTransformDrag(Point pos)
    {
        if (Transform == null) return;
        var pivotCanvas = new SKPoint(_tPivot.X + _tStartOffset.X, _tPivot.Y + _tStartOffset.Y);
        var centerS = ToScreen(pivotCanvas);
        switch (_tDrag)
        {
            case TDrag.Move:
            {
                var a = ToCanvas(_tStartPos);
                var b = ToCanvas(pos);
                _tOffset = new SKPoint(_tStartOffset.X + b.X - a.X, _tStartOffset.Y + b.Y - a.Y);
                break;
            }
            case TDrag.Scale:
            {
                double d0 = Dist(_tStartPos, centerS);
                if (d0 < 2) return;
                float f = (float)(Dist(pos, centerS) / d0);
                _tScaleX = _tStartSX * f;
                _tScaleY = _tStartSY * f;
                break;
            }
            case TDrag.ScaleX:
            case TDrag.ScaleY:
            {
                double rad = _tRot * Math.PI / 180;
                var axis = _tDrag == TDrag.ScaleX
                    ? new SKPoint((float)Math.Cos(rad), (float)Math.Sin(rad))
                    : new SKPoint((float)-Math.Sin(rad), (float)Math.Cos(rad));
                var tip = ToScreen(new SKPoint(pivotCanvas.X + axis.X, pivotCanvas.Y + axis.Y));
                Vector dir = tip - centerS;
                double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                if (len < 1e-6) return;
                dir /= len;
                double d0 = Vector.Dot(_tStartPos - centerS, dir);
                if (Math.Abs(d0) < 2) return;
                float f = (float)(Vector.Dot(pos - centerS, dir) / d0);
                if (_tDrag == TDrag.ScaleX) _tScaleX = _tStartSX * f;
                else _tScaleY = _tStartSY * f;
                break;
            }
            case TDrag.Rotate:
            {
                double a0 = Math.Atan2(_tStartPos.Y - centerS.Y, _tStartPos.X - centerS.X);
                double a1 = Math.Atan2(pos.Y - centerS.Y, pos.X - centerS.X);
                float delta = (float)((a1 - a0) * 180 / Math.PI);
                if (_flipView) delta = -delta;
                _tRot = _tStartRot + delta;
                if (_shift) _tRot = MathF.Round(_tRot / 15f) * 15f;
                break;
            }
        }
        ApplyTransform();
    }

    void DrawTransformHandles(DrawingContext ctx)
    {
        var c = TransformCorners();
        var white = new Pen(Brushes.White, 3);
        for (int i = 0; i < 4; i++)
        {
            ctx.DrawLine(white, c[i], c[(i + 1) % 4]);
            ctx.DrawLine(HandlePen, c[i], c[(i + 1) % 4]);
        }
        var rh = RotateHandle(c);
        ctx.DrawLine(RibbonPen, Mid(c[0], c[1]), rh);
        ctx.DrawEllipse(HandleFill, RibbonPen, rh, 7, 7);
        foreach (var p in c)
            ctx.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - 6, p.Y - 6, 12, 12), 2, 2);
        for (int i = 0; i < 4; i++)
            ctx.DrawEllipse(HandleFill, HandlePen, Mid(c[i], c[(i + 1) % 4]), 5.5, 5.5);
    }

    // ================================================================ utilitaires

    static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
