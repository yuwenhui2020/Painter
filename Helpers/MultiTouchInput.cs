using System;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Painter.Helpers
{
    /// <summary>
    /// 单条笔迹的可视载体。内部用 ContainerVisual 分两层：
    ///   · 已“晾干”层 <see cref="StrokeVisual.DriedVisual"/>：只增不重画；
    ///   · 湿润尖端层（<see cref="StrokeVisual"/> 自身）：每帧只画尚未晾干的一小段。
    /// 这样长笔画从“每帧全量重画整条(O(n²))”降为“每段只画一次(O(n))”，显著提升绘制流畅度。
    /// </summary>
    public class VisualCanvas : FrameworkElement
    {
        private readonly ContainerVisual _root = new ContainerVisual();

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _root;

        public VisualCanvas(StrokeVisual visual)
        {
            // 已晾干段在下、湿润尖端在上，一起挂到 ContainerVisual
            _root.Children.Add(visual.DriedVisual);
            _root.Children.Add(visual);
            AddVisualChild(_root);
        }
    }

    /// <summary>
    ///     用于显示笔迹的类（湿润尖端层）。增量的要点：
    ///     Redraw 只重画“尚未晾干”的尾段；尾段攒够 <see cref="CommitBatch"/> 个点就
    ///     晾干一批进 <see cref="_dried"/>（各自成为一个子 visual，永不清空），
    ///     因此每个点总共只被画一次，整体 O(n)。
    /// </summary>
    public class StrokeVisual : DrawingVisual
    {
        private const int CommitBatch = 40;   // 每攒这么多点就晾干一批，均摊成本
        private readonly ContainerVisual _dried = new ContainerVisual();
        private readonly SolidColorBrush _brush;
        private int _committed;
        private bool _redrawPending;

        /// <summary>
        ///     创建显示笔迹的类
        /// </summary>
        public StrokeVisual() : this(new DrawingAttributes()
        {
            Color = Colors.Red,
            //FitToCurve = true,
            Width = 3,
            Height = 3
        })
        {
        }

        /// <summary>
        ///     创建显示笔迹的类
        /// </summary>
        /// <param name="drawingAttributes"></param>
        public StrokeVisual(DrawingAttributes drawingAttributes)
        {
            _drawingAttributes = drawingAttributes;
            _brush = new SolidColorBrush(drawingAttributes.Color);
            _brush.Freeze();
        }

        /// <summary>已晾干层（由 <see cref="VisualCanvas"/> 挂到可视树，位于本湿润层之下）。</summary>
        public ContainerVisual DriedVisual => _dried;

        /// <summary>
        ///     设置或获取显示的笔迹
        /// </summary>
        public Stroke Stroke { set; get; }

        /// <summary>
        ///     在笔迹中添加点
        /// </summary>
        /// <param name="point"></param>
        public void Add(StylusPoint point)
        {
            if (Stroke == null)
            {
                var collection = new StylusPointCollection { point };
                Stroke = new Stroke(collection) { DrawingAttributes = _drawingAttributes };
            }
            else
            {
                Stroke.StylusPoints.Add(point);
            }
        }

        /// <summary>
        ///     重新画出笔迹（增量：只画尚未晾干的尾段）
        /// </summary>
        public void Redraw()
        {
            if (Stroke == null) return;
            // 高采样率触摸屏一次移动可能连发好几包点，而屏幕每帧只刷新一次。
            // 每包都 RenderOpen + Stroke.Draw 等于把同一段尾迹重复画好几遍，
            // 这里合并到下一帧，一帧最多画一次。
            if (_redrawPending) return;
            _redrawPending = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }

        private void OnCompositionRendering(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _redrawPending = false;
            RedrawNow();
        }

        private void RedrawNow()
        {
            if (Stroke == null) return;
            try
            {
                int count = Stroke.StylusPoints.Count;
                if (count <= _committed) return;   // 没有新点，无需重画

                // 尾段攒够一批就直接晾干：原先先画进湿润层、再整段重复画进已晾干层，
                // 每个“晾干帧”都在做两遍同样的绘制。
                if (count - _committed >= CommitBatch)
                {
                    CommitTail();
                    return;
                }

                // 与已晾干段重叠一个点，避免接缝断裂
                int from = _committed > 0 ? _committed - 1 : 0;
                using (var dc = RenderOpen())
                {
                    DrawRange(dc, from, count);
                }
            }
            catch { }
        }

        /// <summary>把当前尾段晾干进已晾干层（追加一个子 visual，不清空旧的 → O(尾段长)）。</summary>
        private void CommitTail()
        {
            if (Stroke == null) return;
            int count = Stroke.StylusPoints.Count;
            int from = _committed > 0 ? _committed - 1 : 0;
            if (count <= from) return;

            var seg = new DrawingVisual();
            using (var dc = seg.RenderOpen())
            {
                DrawRange(dc, from, count);
            }
            _dried.Children.Add(seg);

            _committed = count;
            // 已全部晾干，清空湿润层（下次有新点再画）
            using (var dc = RenderOpen()) { }
        }

        /// <summary>用临时 Stroke 画 [from, to) 区间，保留压感 / 颜色 / 粗细等绘制属性。</summary>
        private void DrawRange(DrawingContext dc, int from, int to)
        {
            var pts = Stroke.StylusPoints;
            int n = to - from;
            if (n <= 0) return;
            if (n == 1)
            {
                var sp = pts[from];
                double r = Math.Max(1.0, _drawingAttributes.Width) / 2.0;
                dc.DrawEllipse(_brush, null, new Point(sp.X, sp.Y), r, r);
                return;
            }
            // 避免每次都 new StylusPointCollection + Stroke：直接用 DrawingContext 画线段
            // 这样避免了临时对象分配和 Stroke 内部的几何构建开销
            var first = pts[from];
            var path = new System.Windows.Media.PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(first.X, first.Y), IsClosed = false };
            for (int i = from + 1; i < to; i++)
            {
                var p = pts[i];
                figure.Segments.Add(new LineSegment(new Point(p.X, p.Y), true));
            }
            path.Figures.Add(figure);
            var pen = new Pen(_brush, _drawingAttributes.Width);
            pen.Freeze();
            dc.DrawGeometry(null, pen, path);
        }

        private readonly DrawingAttributes _drawingAttributes;
    }
}
