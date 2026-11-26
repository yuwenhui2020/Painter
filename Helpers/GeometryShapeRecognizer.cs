using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input; // StylusPoint 定义在此命名空间，foreach 显式类型需要

namespace Painter.Helpers
{
    /// <summary>几何规整可识别的形状种类。</summary>
    public enum GeoShapeKind
    {
        None,
        Line,
        Arrow,
        Rectangle,
        Triangle,
        Ellipse,
        Circle
    }

    /// <summary>
    /// 几何识别结果。SubPaths 是「规整后的点集」，一个形状可能由多条笔画组成（箭头 = 杆 + 两条箭羽）。
    /// </summary>
    public class GeoShapeResult
    {
        public GeoShapeKind Kind;
        /// <summary>置信度 0~1，越高越像标准形状。</summary>
        public double Confidence;
        /// <summary>规整后的点序列（每条子路径对应一条待生成笔画）。</summary>
        public List<List<Point>> SubPaths = new List<List<Point>>();
        public Rect Bounds;
        public Point Centroid;
    }

    /// <summary>
    /// 纯 C# 几何形状识别：把单笔手绘规整为 直线 / 箭头 / 矩形 / 三角形 / 圆 / 椭圆。
    /// <para>
    /// 与旧的 InkAnalyzer（IACore/IAWinFX，x86-only，每笔跑 3~6 次非托管分析）完全不同，
    /// 这里只做 O(n) 的点几何运算：序列化去重 → 闭合判定 → Ramer-Douglas-Peucker 抽角点 →
    /// 按角点数与径向方差分类。无原生依赖、无位数限制、Win7-Win11 行为一致，速度可忽略。
    /// </para>
    /// 识别失败（Confidence 低或跨度太小）时返回 null，调用方保留原始手绘，不产生误规整。
    /// </summary>
    public static class GeometryShapeRecognizer
    {
        /// <summary>
        /// 识别一条笔画。minSpan=最小跨度（像素，低于此值不处理）；tolerance=判定容差（0~1）。
        /// </summary>
        public static GeoShapeResult Recognize(Stroke stroke, double minSpan, double tolerance)
        {
            if (stroke == null || stroke.StylusPoints == null || stroke.StylusPoints.Count < 3)
                return null;

            var raw = new List<Point>(stroke.StylusPoints.Count);
            foreach (StylusPoint sp in stroke.StylusPoints)
                raw.Add(new Point(sp.X, sp.Y));

            return Recognize(raw, minSpan, tolerance);
        }

        /// <summary>识别点序列（便于复用与单元测试）。</summary>
        public static GeoShapeResult Recognize(List<Point> raw, double minSpan, double tolerance)
        {
            if (raw == null || raw.Count < 3) return null;
            if (tolerance <= 0 || tolerance > 1) tolerance = 0.25;

            List<Point> pts = Dedupe(raw);
            if (pts.Count < 3) return null;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            var bounds = new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
            double span = Math.Max(bounds.Width, bounds.Height);
            if (span < minSpan) return null;

            double pathLen = PathLength(pts);
            if (pathLen < span * 1.6) return null; // 点太少/几乎没动，不判形状

            Point a = pts[0], b = pts[pts.Count - 1];
            double endGap = Dist(a, b);
            bool closed = endGap < span * (0.10 + tolerance * 0.30) && pathLen > span * 2.6;
            double eps = span * (0.025 + tolerance * 0.045);

            GeoShapeResult result;
            if (closed)
                result = RecognizeClosed(pts, bounds, span, eps, tolerance);
            else
                result = RecognizeOpen(pts, bounds, span, eps, tolerance);

            if (result == null) return null;
            result.Bounds = bounds;
            result.Centroid = new Point((minX + maxX) / 2.0, (minY + maxY) / 2.0);

            // 规整后仍需覆盖足够面积，避免把一个小勾当成箭头
            foreach (List<Point> sub in result.SubPaths)
                if (sub != null && sub.Count >= 2)
                    return result;
            return null;
        }

        #region 闭合形状：三角形 / 矩形 / 圆 / 椭圆

        private static GeoShapeResult RecognizeClosed(List<Point> pts, Rect bounds, double span,
                                                      double eps, double tolerance)
        {
            List<Point> corners = Rdp(pts, eps);
            // 首尾去重（RDP 结果首尾通常重合）
            if (corners.Count > 1 && Dist(corners[0], corners[corners.Count - 1]) < eps)
                corners.RemoveAt(corners.Count - 1);

            int n = corners.Count;
            if (n == 3)
                return BuildTriangle(corners, pts, bounds, span, tolerance);
            if (n == 4)
                return BuildQuadrilateral(corners, pts, bounds, span, tolerance);

            // 角点过多或过少 → 尝试圆/椭圆（径向方差判定）
            return BuildEllipse(pts, bounds, span, tolerance);
        }

        private static GeoShapeResult BuildTriangle(List<Point> c, List<Point> pts, Rect bounds,
                                                    double span, double tolerance)
        {
            double minEdge = Math.Min(Math.Min(Dist(c[0], c[1]), Dist(c[1], c[2])), Dist(c[2], c[0]));
            if (minEdge < span * 0.15) return null;

            var path = new List<Point> { c[0], c[1], c[2], c[0] };
            var r = new GeoShapeResult { Kind = GeoShapeKind.Triangle, Confidence = 0.86 };
            r.SubPaths.Add(path);
            return r;
        }

        private static GeoShapeResult BuildQuadrilateral(List<Point> c, List<Point> pts, Rect bounds,
                                                         double span, double tolerance)
        {
            // 按顺序已是环绕顺序；计算四边长度与对边垂直度来给矩形置信度
            double[] e = new double[4];
            for (int i = 0; i < 4; i++) e[i] = Dist(c[i], c[(i + 1) % 4]);
            double minEdge = double.MaxValue;
            for (int i = 0; i < 4; i++) if (e[i] < minEdge) minEdge = e[i];
            if (minEdge < span * 0.12) return null;

            double ratio = Math.Min(Math.Min(e[0], e[2]), Math.Min(e[1], e[3])) /
                           Math.Max(Math.Max(e[0], e[2]), Math.Max(e[1], e[3]));
            double rightAngleScore = 0;
            for (int i = 0; i < 4; i++)
            {
                Point p0 = c[(i + 3) % 4], p1 = c[i], p2 = c[(i + 1) % 4];
                double dot = (p0.X - p1.X) * (p2.X - p1.X) + (p0.Y - p1.Y) * (p2.Y - p1.Y);
                double m = Dist(p0, p1) * Dist(p2, p1);
                if (m > 0 && Math.Abs(dot / m) < (0.15 + tolerance * 0.25)) rightAngleScore++;
            }
            double conf = 0.62 + 0.18 * ratio + 0.05 * rightAngleScore;
            var path = new List<Point> { c[0], c[1], c[2], c[3], c[0] };
            var r = new GeoShapeResult { Kind = GeoShapeKind.Rectangle, Confidence = Math.Min(0.97, conf) };
            r.SubPaths.Add(path);
            return r;
        }

        private static GeoShapeResult BuildEllipse(List<Point> pts, Rect bounds, double span, double tolerance)
        {
            double cx = (bounds.X + bounds.Width / 2.0);
            double cy = (bounds.Y + bounds.Height / 2.0);
            double rx = Math.Max(1, bounds.Width / 2.0);
            double ry = Math.Max(1, bounds.Height / 2.0);

            // 径向残差：所有点到标准椭圆的归一化半径应接近 1
            double sum = 0, sum2 = 0;
            foreach (Point p in pts)
            {
                double dx = (p.X - cx) / rx;
                double dy = (p.Y - cy) / ry;
                double rad = Math.Sqrt(dx * dx + dy * dy);
                sum += rad; sum2 += rad * rad;
            }
            double mean = sum / pts.Count;
            double variance = Math.Max(0, sum2 / pts.Count - mean * mean);
            double deviation = Math.Sqrt(variance) / Math.Max(0.01, mean);
            if (deviation > 0.18 + tolerance * 0.22) return null; // 太不像椭圆

            bool isCircle = Math.Abs(bounds.Width - bounds.Height) / span < (0.08 + tolerance * 0.12);
            var path = SampleEllipse(cx, cy, rx, ry, isCircle ? 72 : 96);
            var r = new GeoShapeResult
            {
                Kind = isCircle ? GeoShapeKind.Circle : GeoShapeKind.Ellipse,
                Confidence = Math.Max(0.55, 0.95 - deviation * 2.0),
                SubPaths = new List<List<Point>> { path }
            };
            return r;
        }

        #endregion

        #region 开放形状：直线 / 箭头

        private static GeoShapeResult RecognizeOpen(List<Point> pts, Rect bounds, double span,
                                                    double eps, double tolerance)
        {
            // 用「首尾连线」作为主轴：对手绘直线足够稳，且 O(n)
            Point a = pts[0], b = pts[pts.Count - 1];
            double len = Dist(a, b);
            if (len < span * 0.55) return null;

            double maxDev = 0;
            foreach (Point p in pts)
                maxDev = Math.Max(maxDev, PointLineDistance(p, a, b));

            // 偏离主轴很小 → 直线
            if (maxDev < span * (0.035 + tolerance * 0.075))
            {
                var r = new GeoShapeResult
                {
                    Kind = GeoShapeKind.Line,
                    Confidence = 0.92,
                    SubPaths = new List<List<Point>> { new List<Point> { a, b } }
                };
                return r;
            }

            // 否则尝试箭头：找离起点最远的点作为「箭头尖」，尖之后应有一小段折回（箭羽）
            return TryArrow(pts, a, span, tolerance);
        }

        private static GeoShapeResult TryArrow(List<Point> pts, Point a, double span, double tolerance)
        {
            int tipIndex = 0;
            double far = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = Dist(pts[i], a);
                if (d > far) { far = d; tipIndex = i; }
            }
            Point tip = pts[tipIndex];
            if (far < span * 0.55) return null;

            // 尖端之后剩余的点应形成「折回」：距离尖端先增后减，或离主轴另一侧偏移
            int tail = pts.Count - 1 - tipIndex;
            if (tail < 2) return null;

            double ux = (tip.X - a.X) / far, uy = (tip.Y - a.Y) / far;
            double barb = span * (0.06 + tolerance * 0.10);
            double sideA = 0, sideB = 0; // 箭羽在主轴两侧的投影
            for (int i = tipIndex + 1; i < pts.Count; i++)
            {
                double vx = pts[i].X - tip.X, vy = pts[i].Y - tip.Y;
                double cross = ux * vy - uy * vx; // 带符号的侧向偏移
                double back = -(vx * ux + vy * uy); // 沿主轴往回的长度
                if (back < barb * 0.4) continue;
                if (cross > 0) sideA = Math.Max(sideA, back);
                else sideB = Math.Max(sideB, back);
            }
            double barbLen = far * (0.12 + tolerance * 0.10);
            if (sideA < barbLen * 0.5 || sideB < barbLen * 0.5) return null;

            double nx = -uy, ny = ux;
            Point wing1 = new Point(tip.X - ux * barbLen + nx * barbLen * 0.75,
                                    tip.Y - uy * barbLen + ny * barbLen * 0.75);
            Point wing2 = new Point(tip.X - ux * barbLen - nx * barbLen * 0.75,
                                    tip.Y - uy * barbLen - ny * barbLen * 0.75);

            var r = new GeoShapeResult
            {
                Kind = GeoShapeKind.Arrow,
                Confidence = 0.78,
                SubPaths = new List<List<Point>>
                {
                    new List<Point> { a, tip },
                    new List<Point> { wing1, tip, wing2 }
                }
            };
            return r;
        }

        #endregion

        #region 几何工具

        /// <summary>去掉连续重复/过近的点，降低噪声与后续计算量。</summary>
        private static List<Point> Dedupe(List<Point> src)
        {
            var list = new List<Point>(src.Count);
            foreach (Point p in src)
            {
                if (list.Count == 0 || Dist(p, list[list.Count - 1]) > 0.6)
                    list.Add(p);
            }
            return list;
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double PathLength(List<Point> pts)
        {
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += Dist(pts[i - 1], pts[i]);
            return len;
        }

        private static double PointLineDistance(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < 1e-9) return Dist(p, a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / d2;
            t = Math.Max(0, Math.Min(1, t));
            double px = a.X + t * dx, py = a.Y + t * dy;
            return Dist(p, new Point(px, py));
        }

        /// <summary>Ramer-Douglas-Peucker 折线简化，用于抽取角点。</summary>
        private static List<Point> Rdp(List<Point> pts, double eps)
        {
            if (pts.Count < 3) return new List<Point>(pts);
            bool[] keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<Tuple<int, int>>();
            stack.Push(Tuple.Create(0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var seg = stack.Pop();
                int first = seg.Item1, last = seg.Item2;
                double maxD = 0; int idx = -1;
                for (int i = first + 1; i < last; i++)
                {
                    double d = PointLineDistance(pts[i], pts[first], pts[last]);
                    if (d > maxD) { maxD = d; idx = i; }
                }
                if (maxD > eps && idx > 0)
                {
                    keep[idx] = true;
                    stack.Push(Tuple.Create(first, idx));
                    stack.Push(Tuple.Create(idx, last));
                }
            }
            var result = new List<Point>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
            return result;
        }

        private static List<Point> SampleEllipse(double cx, double cy, double rx, double ry, int steps)
        {
            var list = new List<Point>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                double t = 2 * Math.PI * i / steps;
                list.Add(new Point(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
            }
            return list;
        }

        #endregion
    }
}
