using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Painter
{
    /// <summary>
    /// 不依赖图标字体的矢量图标。形状来自 Resources/Styles/AppIcons.xaml：
    /// 以“描边主线 + 可选填充块”的方式绘制，颜色沿用 Control.Foreground，
    /// 因此深浅主题切换照常生效。
    /// </summary>
    public class AppIcon : Control
    {
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(AppIcon),
            new PropertyMetadata(null, OnGlyphChanged));

        public static readonly DependencyPropertyKey DataPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(Data), typeof(Geometry), typeof(AppIcon), new PropertyMetadata(null));

        public static readonly DependencyProperty DataProperty = DataPropertyKey.DependencyProperty;

        public static readonly DependencyPropertyKey FillDataPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(FillData), typeof(Geometry), typeof(AppIcon), new PropertyMetadata(null));

        public static readonly DependencyProperty FillDataProperty = FillDataPropertyKey.DependencyProperty;

        /// <summary>图标名，对应资源键 Icon{Glyph}Geometry / Icon{Glyph}FillGeometry。</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>描边主线，模板里的 Path 以 Stroke 绘制。</summary>
        public Geometry Data => (Geometry)GetValue(DataProperty);

        /// <summary>需要实心填充的部分（三角、圆点等），没有则为空。</summary>
        public Geometry FillData => (Geometry)GetValue(FillDataProperty);

        private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((AppIcon)d).ResolveGeometry();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Glyph 在 XAML 解析阶段被赋值时控件还不属于任何树，资源查不到；
            // 模板应用时是最后一次机会，此时祖先与应用资源都已就绪。
            if (Data == null || FillData == null) ResolveGeometry();
        }

        void ResolveGeometry()
        {
            string glyph = Glyph;
            if (string.IsNullOrEmpty(glyph)) return;
            // 两个几何都要重置：像 Pause 这种只有填充块的图标若沿用上一个图标的
            // 描边主线，就会在两条竖线后面留下一个三角形。
            SetValue(DataPropertyKey, LookupGeometry("Icon" + glyph + "Geometry"));
            SetValue(FillDataPropertyKey, LookupGeometry("Icon" + glyph + "FillGeometry"));
        }

        Geometry LookupGeometry(string key)
        {
            object found = TryFindResource(key);
            if (found == null && Application.Current != null)
            {
                try { found = Application.Current.Resources[key]; }
                catch { }
            }
            return found as Geometry;
        }
    }
}
