using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Ink;

namespace Painter.Helpers
{
    /// <summary>
    /// 智能笔迹平滑：对收笔后的点序列做轻量低通滤波，去掉手指/鼠标的细微抖动，
    /// 让教学板书更顺滑。设计要点：
    /// <list type="bullet">
    /// <item>保留首尾端点，笔迹起点/落点不漂移；</item>
    /// <item>只平移点的 X/Y，不改 PressureFactor，笔锋逻辑不受影响；</item>
    /// <item>点数太少（≤4）直接跳过，避免短笔画被过度平滑；</item>
    /// <item>强度可配（0~1）；一次性 O(n)，低配机也无感。</item>
    /// </list>
    /// 纯托管实现，Win7-Win11 一致。
    /// </summary>
    public static class StrokeSmoother
    {
        /// <summary>就地平滑一条笔画（返回新的 StylusPointCollection，调用方赋值回 Stroke.StylusPoints）。</summary>
        public static StylusPointCollection Smooth(StylusPointCollection src, double strength)
        {
            if (src == null || src.Count <= 4) return src;
            if (strength <= 0.01) return src;
            if (strength > 1) strength = 1;

            // 一次「保端点」的移动加权平均：new[i] = (1-k)*p[i] + k*(p[i-1]+p[i+1])/2
            // 比 Chaikin 更少顶点膨胀，笔迹更贴近原始手写轨迹。
            int n = src.Count;
            var result = new StylusPointCollection();
            StylusPoint first = src[0], last = src[n - 1];
            result.Add(first);

            for (int i = 1; i < n - 1; i++)
            {
                StylusPoint prev = src[i - 1], cur = src[i], next = src[i + 1];
                double x = (1 - strength) * cur.X + strength * (prev.X + next.X) / 2.0;
                double y = (1 - strength) * cur.Y + strength * (prev.Y + next.Y) / 2.0;
                // 保留原压力/按钮位，仅改坐标
                var sp = new StylusPoint(x, y, cur.PressureFactor);
                result.Add(sp);
            }
            result.Add(last);
            return result;
        }

        /// <summary>判断笔画是否来自真实压感笔（这类笔精度高，不需要平滑）。</summary>
        public static bool IsPressureSensitive(StylusPointCollection points)
        {
            if (points == null) return false;
            foreach (StylusPoint p in points)
            {
                if (p.PressureFactor != 0.5f && p.PressureFactor != 0f)
                    return true;
            }
            return false;
        }
    }
}
