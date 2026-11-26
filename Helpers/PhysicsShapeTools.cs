using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Painter.Helpers
{
    /// <summary>
    /// 物理教学图形工具（Win7-Win11 通用，纯 C# / WPF 标准 API，不依赖 x86 识别引擎）。
    ///
    /// 能力：
    ///  1) 判定一条笔迹是否是"闭合圆/椭圆"（点选圆后弹菜单用）；
    ///  2) 在圆内生成磁场符号阵列：⊙ 点 = 垂直纸面向外，⊗ 叉 = 垂直纸面向里；
    ///     按规则网格整整齐齐铺满若干个，数量随圆的大小自动增减并裁剪在圆内；
    ///  3) 用 Stroke.AddPropertyData 把填充笔迹绑定到所属圆，切换/清除时精准定位；
    ///  4) 生成常用电磁学符号笔迹：电流箭头、电阻、电容、电感、二极管、LED、接地、电源、开关。
    ///
    /// 返回的全部是普通 Stroke，可直接加入 InkCanvas.Strokes，走既有保存/撤销体系。
    /// </summary>
    internal static class PhysicsShapeTools
    {
        // ---- PropertyData 键：ISF 保存支持 Guid/string/bool 等基础类型，会随笔迹一起持久化 ----
        private static readonly Guid CircleIdProperty = new Guid("6E5A1001-9C3F-4A11-8F20-1D2E3F4A5B60");
        private static readonly Guid FillOwnerProperty = new Guid("6E5A1002-9C3F-4A11-8F20-1D2E3F4A5B61");
        private static readonly Guid FillKindProperty = new Guid("6E5A1003-9C3F-4A11-8F20-1D2E3F4A5B62");
        private static readonly Guid PhysicsSymbolProperty = new Guid("6E5A1004-9C3F-4A11-8F20-1D2E3F4A5B63");

        public const string FillKindDot = "dot";    // ⊙ 出纸面
        public const string FillKindCross = "cross"; // ⊗ 入纸面

        #region 圆的判定

        /// <summary>
        /// 用点序列的包围盒 + 径向方差判定是否为闭合圆/椭圆。
        /// 比 InkCanvas.HitTest 更贴合需求：只认"画出来是圆"的笔迹，普通涂鸦不会误触发。
        /// </summary>
        public static bool TryGetCircleBounds(Stroke stroke, out Point center, out double rx, out double ry)
        {
            center = new Point();
            rx = ry = 0;
            if (stroke == null) return false;

            var pts = stroke.StylusPoints;
            int n = pts.Count;
            if (n < 12) return false; // 圆由 GenerateEllipseGeometry 生成，点数远超此值

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double x = pts[i].X, y = pts[i].Y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            double w = maxX - minX, h = maxY - minY;
            if (w < 12 || h < 12) return false; // 太小，无填充意义

            // 首尾闭合度：首尾点距离不超过短轴的 25%
            double dx = pts[0].X - pts[n - 1].X;
            double dy = pts[0].Y - pts[n - 1].Y;
            double closure = Math.Sqrt(dx * dx + dy * dy);
            if (closure > Math.Min(w, h) * 0.25) return false;

            center = new Point((minX + maxX) / 2.0, (minY + maxY) / 2.0);
            rx = w / 2.0;
            ry = h / 2.0;

            // 径向方差：归一化到单位圆后，各点到中心半径应接近 1
            double sum = 0, sumSq = 0;
            int samples = 0;
            int step = Math.Max(1, n / 96); // 最多取样 96 点， O(1) 常量级开销
            for (int i = 0; i < n; i += step)
            {
                double ux = (pts[i].X - center.X) / rx;
                double uy = (pts[i].Y - center.Y) / ry;
                double r = Math.Sqrt(ux * ux + uy * uy);
                sum += r;
                sumSq += r * r;
                samples++;
            }
            if (samples < 8) return false;
            double mean = sum / samples;
            double var = Math.Max(0, sumSq / samples - mean * mean);
            double std = Math.Sqrt(var);
            // 圆/椭圆径向 std 很小；矩形/折线显著偏大。0.22 兼顾手绘误差与误判抑制
            return std <= 0.22 && mean >= 0.75 && mean <= 1.25;
        }

        #endregion

        #region 关联标记

        private static Guid GetOrCreateCircleId(Stroke circle)
        {
            Guid id;
            if (circle.ContainsPropertyData(CircleIdProperty))
            {
                object o = circle.GetPropertyData(CircleIdProperty);
                if (o is Guid) return (Guid)o;
                if (o != null)
                {
                    Guid parsed;
                    if (Guid.TryParse(o.ToString(), out parsed)) return parsed;
                }
            }
            id = Guid.NewGuid();
            try { circle.AddPropertyData(CircleIdProperty, id); } catch { }
            return id;
        }

        /// <summary>找出画布中绑定到该圆的全部填充笔迹（用于切换类型或清除）。</summary>
        public static List<Stroke> FindFills(StrokeCollection strokes, Stroke circle)
        {
            var result = new List<Stroke>();
            if (strokes == null || circle == null) return result;
            if (!circle.ContainsPropertyData(CircleIdProperty)) return result;
            object circleObj = circle.GetPropertyData(CircleIdProperty);
            string owner = circleObj == null ? null : circleObj.ToString();
            if (string.IsNullOrEmpty(owner)) return result;

            foreach (Stroke s in strokes)
            {
                if (s == null || !s.ContainsPropertyData(FillOwnerProperty)) continue;
                object o = s.GetPropertyData(FillOwnerProperty);
                if (o != null && string.Equals(o.ToString(), owner, StringComparison.Ordinal))
                    result.Add(s);
            }
            return result;
        }

        #endregion

        #region 磁场填充

        /// <summary>
        /// 在圆内铺满 ⊙/⊗ 符号阵列。dot = ⊙ 出纸面（箭头尖朝你，看到点）；cross = ⊗ 入纸面（看到箭羽叉）。
        /// 符号按规则网格整齐排列，个数随圆的大小自动增减，且全部裁剪在圆内。
        /// 每个符号的笔迹都带上所属圆标记，切换类型 / 清除时能被 FindFills 一次性找到。
        /// </summary>
        public static StrokeCollection GenerateFieldFill(Stroke circle, string fillKind, DrawingAttributes template)
        {
            var result = new StrokeCollection();
            Point center;
            double rx, ry;
            if (!TryGetCircleBounds(circle, out center, out rx, out ry)) return result;

            Guid id = GetOrCreateCircleId(circle);
            double shortAxis = Math.Min(rx, ry);

            DrawingAttributes da = template != null ? template.Clone() : new DrawingAttributes();
            da.IsHighlighter = false;
            // 填充笔画比轮廓略细，视觉层次更好；下限保证小圆也可见
            da.Width = Math.Max(1.5, Math.Min(da.Width, Math.Max(2.0, shortAxis * 0.05)));
            da.Height = da.Width;

            // 符号半径几乎固定（教学板书里符号大小应一致），只在极小/极大圆时微调，
            // 这样"圆越大 → 格子越多 → 符号越多"，符合物理课本的均匀磁场画法。
            double symbolR = Math.Max(4.0, Math.Min(7.0, shortAxis * 0.07));
            double spacing = Math.Max(16.0, symbolR * 3.0);

            // 先按当前间距估算格子数量；过多时自动放大间距，避免超大圆生成上千笔迹卡顿。
            // 用内切椭圆包围盒估算，略偏大是安全的（后面还会按椭圆精确裁剪）。
            const int MaxSymbols = 420;
            int colsEst = Math.Max(1, (int)Math.Floor(2 * rx / spacing) + 1);
            int rowsEst = Math.Max(1, (int)Math.Floor(2 * ry / spacing) + 1);
            if (colsEst * rowsEst > MaxSymbols)
            {
                double scale = Math.Sqrt((colsEst * rowsEst) / (double)MaxSymbols);
                spacing *= scale;
            }

            // 网格以圆心为中心对称排布，奇数行/列正好落在圆心上，视觉更整齐
            int maxI = Math.Max(0, (int)Math.Floor((rx - symbolR * 1.2) / spacing));
            int maxJ = Math.Max(0, (int)Math.Floor((ry - symbolR * 1.2) / spacing));
            double innerRx = Math.Max(1.0, rx - symbolR * 1.2);
            double innerRy = Math.Max(1.0, ry - symbolR * 1.2);

            int added = 0;
            for (int j = -maxJ; j <= maxJ; j++)
            {
                double y = center.Y + j * spacing;
                for (int i = -maxI; i <= maxI; i++)
                {
                    double x = center.X + i * spacing;
                    // 椭圆裁剪：符号整体留在圆内（用缩小后的内切椭圆判定）
                    double nx = (x - center.X) / innerRx;
                    double ny = (y - center.Y) / innerRy;
                    if (nx * nx + ny * ny > 1.0) continue;

                    if (fillKind == FillKindDot)
                    {
                        // ⊙：实心圆点 —— 用极短、大笔尖的一笔模拟，StylusTip=Ellipse 天然画成圆点
                        DrawingAttributes d = da.Clone();
                        d.Width = d.Height = Math.Max(4.0, symbolR * 1.5);
                        d.StylusTip = StylusTip.Ellipse;
                        var pts = new StylusPointCollection();
                        pts.Add(new StylusPoint(x, y, 1.0f));
                        pts.Add(new StylusPoint(x + 0.01, y + 0.01, 1.0f));
                        Stroke dot = new Stroke(pts) { DrawingAttributes = d };
                        TagFill(dot, id, FillKindDot);
                        result.Add(dot);
                    }
                    else // cross
                    {
                        // ⊗：圆内交叉的两条线（X），四端都限制在符号半径内
                        double d = symbolR * 0.75;
                        AddLine(result, x - d, y - d, x + d, y + d, da, id, FillKindCross);
                        AddLine(result, x + d, y - d, x - d, y + d, da, id, FillKindCross);
                    }
                    added++;
                    if (added >= MaxSymbols) break;
                }
                if (added >= MaxSymbols) break;
            }

            // 极端小圆（比一个符号还小）时至少留一个，保证点击有反馈
            if (added == 0)
            {
                if (fillKind == FillKindDot)
                {
                    DrawingAttributes d = da.Clone();
                    d.Width = d.Height = Math.Max(4.0, symbolR * 1.5);
                    d.StylusTip = StylusTip.Ellipse;
                    var pts = new StylusPointCollection();
                    pts.Add(new StylusPoint(center.X, center.Y, 1.0f));
                    pts.Add(new StylusPoint(center.X + 0.01, center.Y + 0.01, 1.0f));
                    Stroke dot = new Stroke(pts) { DrawingAttributes = d };
                    TagFill(dot, id, FillKindDot);
                    result.Add(dot);
                }
                else
                {
                    double d = Math.Max(3.0, shortAxis * 0.4);
                    AddLine(result, center.X - d, center.Y - d, center.X + d, center.Y + d, da, id, FillKindCross);
                    AddLine(result, center.X + d, center.Y - d, center.X - d, center.Y + d, da, id, FillKindCross);
                }
            }
            return result;
        }

        private static void TagFill(Stroke s, Guid ownerId, string kind)
        {
            try
            {
                s.AddPropertyData(FillOwnerProperty, ownerId.ToString());
                s.AddPropertyData(FillKindProperty, kind);
            }
            catch { }
        }

        private static void AddLine(StrokeCollection target, double x1, double y1, double x2, double y2,
                                    DrawingAttributes da, Guid ownerId, string kind)
        {
            var pts = new StylusPointCollection();
            pts.Add(new StylusPoint(x1, y1, 1.0f));
            pts.Add(new StylusPoint(x2, y2, 1.0f));
            Stroke s = new Stroke(pts) { DrawingAttributes = da.Clone() };
            TagFill(s, ownerId, kind);
            target.Add(s);
        }

        #endregion

        #region 常用物理/电磁符号

        public enum PhysicsSymbol
        {
            CurrentArrow,   // 电流方向箭头 →
            Resistor,       // 电阻（矩形符号）
            Capacitor,      // 电容（两极板）
            Inductor,       // 电感（线圈）
            Diode,          // 二极管
            Led,            // 发光二极管（二极管 + 出射箭头）
            Ground,         // 接地
            Battery,        // 电源（长正短负）
            Switch          // 开关
        }

        /// <summary>在指定中心生成物理符号笔迹。size = 符号整体尺寸（像素）。</summary>
        public static StrokeCollection GenerateSymbol(PhysicsSymbol kind, Point center, double size, DrawingAttributes template)
        {
            var result = new StrokeCollection();
            if (size < 12) size = 12;
            DrawingAttributes da = template != null ? template.Clone() : new DrawingAttributes();
            da.IsHighlighter = false;
            da.Width = da.Height = Math.Max(1.2, Math.Min(da.Width, size / 22.0));
            double h = size / 2.0;

            switch (kind)
            {
                case PhysicsSymbol.CurrentArrow:
                {
                    AddLine(result, center.X - h, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X + h - size * 0.18, center.Y - size * 0.14, center.X + h, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X + h - size * 0.18, center.Y + size * 0.14, center.X + h, center.Y, da, Guid.Empty, null, false);
                    break;
                }
                case PhysicsSymbol.Resistor:
                {
                    double rw = size * 0.62, rh = size * 0.26;
                    AddPoly(result, da, false,
                        new Point(center.X - h, center.Y),
                        new Point(center.X - rw / 2, center.Y),
                        new Point(center.X - rw / 2, center.Y - rh / 2),
                        new Point(center.X + rw / 2, center.Y - rh / 2),
                        new Point(center.X + rw / 2, center.Y + rh / 2),
                        new Point(center.X - rw / 2, center.Y + rh / 2),
                        new Point(center.X - rw / 2, center.Y),
                        new Point(center.X + h, center.Y));
                    break;
                }
                case PhysicsSymbol.Capacitor:
                {
                    double gap = size * 0.10, plate = size * 0.34;
                    AddLine(result, center.X - h, center.Y, center.X - gap, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X + gap, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X - gap, center.Y - plate / 2, center.X - gap, center.Y + plate / 2, da, Guid.Empty, null, false);
                    AddLine(result, center.X + gap, center.Y - plate / 2, center.X + gap, center.Y + plate / 2, da, Guid.Empty, null, false);
                    break;
                }
                case PhysicsSymbol.Inductor:
                {
                    double lead = size * 0.20, r = size * 0.13;
                    AddLine(result, center.X - h, center.Y, center.X - h + lead, center.Y, da, Guid.Empty, null, false);
                    var arc = new StylusPointCollection();
                    int arcPts = 10;
                    double span = size * 0.60;
                    for (int i = 0; i <= arcPts; i++)
                    {
                        double t = i / (double)arcPts;
                        double x = center.X - span / 2 + span * t;
                        double y = center.Y - r * Math.Sin(t * Math.PI * 4);
                        arc.Add(new StylusPoint(x, y, 1.0f));
                    }
                    Stroke coil = new Stroke(arc) { DrawingAttributes = da.Clone() };
                    result.Add(coil);
                    AddLine(result, center.X + h - lead, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    break;
                }
                case PhysicsSymbol.Diode:
                case PhysicsSymbol.Led:
                {
                    double tri = size * 0.28;
                    AddLine(result, center.X - h, center.Y, center.X - tri, center.Y, da, Guid.Empty, null, false);
                    AddPoly(result, da, false,
                        new Point(center.X - tri, center.Y - tri * 0.62),
                        new Point(center.X - tri, center.Y + tri * 0.62),
                        new Point(center.X + tri * 0.35, center.Y));
                    AddLine(result, center.X + tri * 0.35, center.Y - tri * 0.7, center.X + tri * 0.35, center.Y + tri * 0.7, da, Guid.Empty, null, false);
                    AddLine(result, center.X + tri * 0.35, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    if (kind == PhysicsSymbol.Led)
                    {
                        AddLine(result, center.X + tri * 0.75, center.Y - tri * 0.9, center.X + tri * 1.25, center.Y - tri * 1.4, da, Guid.Empty, null, false);
                        AddLine(result, center.X + tri * 1.25, center.Y - tri * 1.4, center.X + tri * 1.05, center.Y - tri * 1.45, da, Guid.Empty, null, false);
                        AddLine(result, center.X + tri * 1.25, center.Y - tri * 1.4, center.X + tri * 1.3, center.Y - tri * 1.2, da, Guid.Empty, null, false);
                    }
                    break;
                }
                case PhysicsSymbol.Ground:
                {
                    double w1 = size * 0.62, w2 = size * 0.40, w3 = size * 0.18;
                    double y0 = center.Y - size * 0.14, gapY = size * 0.14;
                    AddLine(result, center.X - w1 / 2, y0, center.X + w1 / 2, y0, da, Guid.Empty, null, false);
                    AddLine(result, center.X - w2 / 2, y0 + gapY, center.X + w2 / 2, y0 + gapY, da, Guid.Empty, null, false);
                    AddLine(result, center.X - w3 / 2, y0 + gapY * 2, center.X + w3 / 2, y0 + gapY * 2, da, Guid.Empty, null, false);
                    break;
                }
                case PhysicsSymbol.Battery:
                {
                    double longPlate = size * 0.52, shortPlate = size * 0.28, gap = size * 0.12;
                    AddLine(result, center.X - h, center.Y, center.X - gap, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X + gap, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X - gap, center.Y - longPlate / 2, center.X - gap, center.Y + longPlate / 2, da, Guid.Empty, null, false);
                    AddLine(result, center.X + gap, center.Y - shortPlate / 2, center.X + gap, center.Y + shortPlate / 2, da, Guid.Empty, null, false);
                    break;
                }
                case PhysicsSymbol.Switch:
                {
                    double open = size * 0.30, dy = size * 0.20;
                    AddLine(result, center.X - h, center.Y, center.X - open, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X + open, center.Y, center.X + h, center.Y, da, Guid.Empty, null, false);
                    AddLine(result, center.X - open, center.Y, center.X + open - size * 0.05, center.Y - dy, da, Guid.Empty, null, false);
                    break;
                }
            }

            foreach (Stroke s in result) TagSymbol(s, kind.ToString());
            return result;
        }

        private static void TagSymbol(Stroke s, string kind)
        {
            try { s.AddPropertyData(PhysicsSymbolProperty, kind); } catch { }
        }

        public static bool IsPhysicsSymbol(Stroke s)
        {
            return s != null && s.ContainsPropertyData(PhysicsSymbolProperty);
        }

        private static void AddPoly(StrokeCollection target, DrawingAttributes da, bool close, params Point[] pts)
        {
            var sp = new StylusPointCollection();
            for (int i = 0; i < pts.Length; i++) sp.Add(new StylusPoint(pts[i].X, pts[i].Y, 1.0f));
            if (close && pts.Length > 0) sp.Add(new StylusPoint(pts[0].X, pts[0].Y, 1.0f));
            target.Add(new Stroke(sp) { DrawingAttributes = da.Clone() });
        }

        private static void AddLine(StrokeCollection target, double x1, double y1, double x2, double y2,
                                    DrawingAttributes da, Guid ownerId, string kind, bool tag)
        {
            var pts = new StylusPointCollection();
            pts.Add(new StylusPoint(x1, y1, 1.0f));
            pts.Add(new StylusPoint(x2, y2, 1.0f));
            Stroke s = new Stroke(pts) { DrawingAttributes = da.Clone() };
            if (tag) TagFill(s, ownerId, kind);
            target.Add(s);
        }

        #endregion
    }
}
