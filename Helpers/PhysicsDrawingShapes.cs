using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace Painter.Helpers
{
    /// <summary>
    /// 物理教学常用图形的"拖拽绘制"生成器（Win7-Win11 通用，纯 WPF 标准 API）。
    ///
    /// 与 PhysicsShapeTools（点选圆后填充磁场 / 插入电路符号）互补：
    /// 本类负责"在画布上按住拖动画出一个完整物理图元"，返回普通 StrokeCollection，
    /// 可直接交给 InkCanvas 走既有的保存 / 撤销体系。
    ///
    /// mode 与 MainWindow.drawingShapeMode 一一对应（从 33 起，避免占用既有模式）。
    /// </summary>
    internal static class PhysicsDrawingShapes
    {
        // 与 MainWindow.xaml.cs 中 drawingShapeMode 的取值保持一致
        public const int Battery = 33;         // 直流电源 / 电池
        public const int Resistor = 34;        // 定值电阻（矩形，IEC 风格）
        public const int Switch = 35;          // 开关（断开）
        public const int Bulb = 36;            // 小灯泡
        public const int SineWave = 37;        // 正弦波形（交流信号）
        public const int Spring = 38;          // 弹簧 / 螺线管侧视
        public const int PositiveCharge = 39;  // 正点电荷 ⊕
        public const int NegativeCharge = 40;  // 负点电荷 ⊖
        public const int MagneticFieldOut = 41; // 磁场方向向外 ⊙（箭尖朝向观察者，圆内一个点）
        public const int MagneticFieldIn = 42;  // 磁场方向向里 ⊗（箭羽背向观察者，圆内一个叉）

        /// <summary>根据模式与拖拽起止点生成图元；点太小或模式未知时返回空集合。</summary>
        public static StrokeCollection Generate(int mode, Point start, Point end, DrawingAttributes template)
        {
            var result = new StrokeCollection();
            double left = Math.Min(start.X, end.X);
            double right = Math.Max(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);
            double bottom = Math.Max(start.Y, end.Y);
            double w = right - left, h = bottom - top;
            if (w < 10 || h < 10) return result;

            DrawingAttributes da = template != null ? template.Clone() : new DrawingAttributes();
            double cx = (left + right) / 2.0;
            double cy = (top + bottom) / 2.0;

            switch (mode)
            {
                case Battery:
                {
                    // 长线为正、短线为负，中间留空隙；两侧引线接到电路
                    double gap = Math.Max(6, w * 0.08);
                    double longH = h * 0.9, shortH = h * 0.45;
                    double xPos = cx - gap, xNeg = cx + gap;
                    AddLine(result, left, cy, xPos, cy, da);
                    AddLine(result, xPos, cy - longH / 2, xPos, cy + longH / 2, da);
                    AddLine(result, xNeg, cy - shortH / 2, xNeg, cy + shortH / 2, da);
                    AddLine(result, xNeg, cy, right, cy, da);
                    break;
                }
                case Resistor:
                {
                    double bw = w * 0.55, bh = h * 0.5;
                    double bx1 = cx - bw / 2, bx2 = cx + bw / 2;
                    AddLine(result, left, cy, bx1, cy, da);
                    AddLine(result, bx2, cy, right, cy, da);
                    var rect = new List<Point>
                    {
                        new Point(bx1, cy - bh / 2), new Point(bx2, cy - bh / 2),
                        new Point(bx2, cy + bh / 2), new Point(bx1, cy + bh / 2),
                        new Point(bx1, cy - bh / 2)
                    };
                    AddPoly(result, rect, da);
                    break;
                }
                case Switch:
                {
                    double d = Math.Max(5, h * 0.12);
                    double x1 = left + w * 0.28, x2 = right - w * 0.28;
                    // 两端接线柱（小圆点用极短粗线模拟）
                    AddLine(result, left, cy, x1, cy, da);
                    AddLine(result, x2, cy, right, cy, da);
                    AddDot(result, x1, cy, Math.Max(3, d * 0.7), da);
                    AddDot(result, x2, cy, Math.Max(3, d * 0.7), da);
                    // 斜向上的闸刀（断开状态）
                    AddLine(result, x1, cy - d * 0.2, x2 - w * 0.06, cy - h * 0.42, da);
                    break;
                }
                case Bulb:
                {
                    double r = Math.Min(w, h) * 0.36;
                    AddCircle(result, cx, cy, r, da);
                    double k = r * 0.66;
                    AddLine(result, cx - k, cy - k, cx + k, cy + k, da);
                    AddLine(result, cx - k, cy + k, cx + k, cy - k, da);
                    break;
                }
                case SineWave:
                {
                    int samples = Math.Max(40, (int)(w / 3));
                    var pts = new List<Point>(samples + 1);
                    double amp = h * 0.45;
                    for (int i = 0; i <= samples; i++)
                    {
                        double t = i / (double)samples;
                        double x = left + w * t;
                        double y = cy - amp * Math.Sin(t * Math.PI * 4);
                        pts.Add(new Point(x, y));
                    }
                    AddPoly(result, pts, da);
                    break;
                }
                case Spring:
                {
                    int coils = Math.Max(4, (int)(w / 26));
                    int steps = coils * 2;
                    var pts = new List<Point>(steps + 3);
                    pts.Add(new Point(left, cy));
                    double amp = h * 0.42;
                    for (int i = 1; i < steps; i++)
                    {
                        double x = left + w * i / (double)steps;
                        double y = cy + (i % 2 == 0 ? -amp : amp);
                        pts.Add(new Point(x, y));
                    }
                    pts.Add(new Point(right, cy));
                    AddPoly(result, pts, da);
                    break;
                }
                case PositiveCharge:
                case NegativeCharge:
                {
                    double r = Math.Min(w, h) * 0.36;
                    AddCircle(result, cx, cy, r, da);
                    double arm = r * 0.5;
                    AddLine(result, cx - arm, cy, cx + arm, cy, da);
                    if (mode == PositiveCharge) AddLine(result, cx, cy - arm, cx, cy + arm, da);
                    break;
                }
                case MagneticFieldOut:
                case MagneticFieldIn:
                {
                    // 一片均匀磁场：在拖拽出的矩形区域里整齐铺满若干个点或叉，
                    // 数量随所画区域大小自动增减（间距固定，区域越大格子越多）。
                    // 按课本简化画法：只画点/叉本身，不再给每个符号套圆圈。
                    double symbolR = Math.Max(5.0, Math.Min(10.0, Math.Min(w, h) * 0.09));
                    double spacing = symbolR * 3.0;
                    const int MaxSymbols = 400;

                    int cols = Math.Max(1, (int)Math.Floor(w / spacing) + 1);
                    int rows = Math.Max(1, (int)Math.Floor(h / spacing) + 1);
                    if (cols * rows > MaxSymbols)
                    {
                        double scale = Math.Sqrt((cols * rows) / (double)MaxSymbols);
                        spacing *= scale;
                        cols = Math.Max(1, (int)Math.Floor(w / spacing) + 1);
                        rows = Math.Max(1, (int)Math.Floor(h / spacing) + 1);
                    }

                    // 网格居中对称排布
                    double startX = cx - (cols - 1) * spacing / 2.0;
                    double startY = cy - (rows - 1) * spacing / 2.0;
                    int added = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        double y = startY + r * spacing;
                        for (int c = 0; c < cols; c++)
                        {
                            double x = startX + c * spacing;
                            if (mode == MagneticFieldOut)
                            {
                                // 向外：实心点（箭尖朝观察者），去掉外圈
                                AddDot(result, x, y, Math.Max(3.5, symbolR * 0.32), da);
                            }
                            else
                            {
                                // 向里：叉号（箭羽背向观察者），去掉外圈
                                double arm = Math.Max(3.5, symbolR * 0.55);
                                AddLine(result, x - arm, y - arm, x + arm, y + arm, da);
                                AddLine(result, x + arm, y - arm, x - arm, y + arm, da);
                            }
                            added++;
                            if (added >= MaxSymbols) break;
                        }
                        if (added >= MaxSymbols) break;
                    }
                    break;
                }
            }
            return result;
        }

        private static void AddLine(StrokeCollection target, double x1, double y1, double x2, double y2, DrawingAttributes da)
        {
            var pts = new StylusPointCollection();
            pts.Add(new StylusPoint(x1, y1, 1.0f));
            pts.Add(new StylusPoint(x2, y2, 1.0f));
            target.Add(new Stroke(pts) { DrawingAttributes = da.Clone() });
        }

        private static void AddPoly(StrokeCollection target, IEnumerable<Point> points, DrawingAttributes da)
        {
            var pts = new StylusPointCollection();
            foreach (Point p in points) pts.Add(new StylusPoint(p.X, p.Y, 1.0f));
            target.Add(new Stroke(pts) { DrawingAttributes = da.Clone() });
        }

        private static void AddCircle(StrokeCollection target, double cx, double cy, double r, DrawingAttributes da)
        {
            const int segments = 48;
            var pts = new StylusPointCollection(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double t = i * 2.0 * Math.PI / segments;
                pts.Add(new StylusPoint(cx + r * Math.Cos(t), cy + r * Math.Sin(t), 1.0f));
            }
            target.Add(new Stroke(pts) { DrawingAttributes = da.Clone() });
        }

        private static void AddDot(StrokeCollection target, double cx, double cy, double r, DrawingAttributes da)
        {
            var pts = new StylusPointCollection();
            pts.Add(new StylusPoint(cx, cy, 1.0f));
            pts.Add(new StylusPoint(cx + 0.01, cy + 0.01, 1.0f));
            DrawingAttributes d = da.Clone();
            d.Width = d.Height = Math.Max(3.0, r * 2);
            d.StylusTip = StylusTip.Ellipse;
            target.Add(new Stroke(pts) { DrawingAttributes = d });
        }
    }
}
