using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace Painter.Helpers
{
    public class InkRecognizeHelper
    {
        //识别形状
        public static ShapeRecognizeResult RecognizeShape(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0)
                return default;

            // InkAnalyzer 持有非托管识别上下文，Dispose 必须放在 finally：
            // 下面 GetAlternates / AlternateNodes[0] / GetShapeName 任一抛异常都会跳过直线流程的 Dispose。
            // 该方法在 painterCanvas_StrokeCollected 里每笔会调用多次，是最热的路径之一。
            AnalysisAlternate analysisAlternate = null;
            var analyzer = new InkAnalyzer();
            try
            {
                analyzer.AddStrokes(strokes);
                analyzer.SetStrokesType(strokes, System.Windows.Ink.StrokeType.Drawing);

                int strokesCount = strokes.Count;
                var sfsaf = analyzer.Analyze();
                if (sfsaf.Successful)
                {
                    var alternates = analyzer.GetAlternates();
                    if (alternates.Count > 0)
                    {
                        while ((!alternates[0].Strokes.Contains(strokes.Last()) ||
                            !IsContainShapeType(GetDrawingShapeName(alternates[0])))
                            && strokesCount >= 2)
                        {
                            analyzer.RemoveStroke(strokes[strokes.Count - strokesCount]);
                            strokesCount--;
                            sfsaf = analyzer.Analyze();
                            if (sfsaf.Successful)
                            {
                                alternates = analyzer.GetAlternates();
                            }
                        }
                        analysisAlternate = alternates[0];
                    }
                }
            }
            finally
            {
                analyzer.Dispose();
            }

            if (analysisAlternate != null && analysisAlternate.AlternateNodes.Count > 0)
            {
                var node = analysisAlternate.AlternateNodes[0] as InkDrawingNode;
                // 备选节点未必是 InkDrawingNode，as 可能返回 null，直接取 Centroid 会空引用
                if (node != null)
                    return new ShapeRecognizeResult(node.Centroid, node.HotPoints, analysisAlternate, node);
            }

            return default;
        }

        /// <summary>安全读取备选结果的绘图形状名：AlternateNodes 可能为空或首节点不是绘图节点。</summary>
        private static string GetDrawingShapeName(AnalysisAlternate alternate)
        {
            var nodes = alternate?.AlternateNodes;
            if (nodes == null || nodes.Count == 0) return "";
            var node = nodes[0] as InkDrawingNode;
            return node?.GetShapeName() ?? "";
        }

        public static bool IsContainShapeType(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Contains("Triangle") || name.Contains("Circle") ||
                name.Contains("Rectangle") || name.Contains("Diamond") ||
                name.Contains("Parallelogram") || name.Contains("Square")
                || name.Contains("Ellipse"))
            {
                return true;
            }
            return false;
        }
    }

    //Recognizer 的实现

    public enum RecognizeLanguage
    {
        SimplifiedChinese = 0x0804,
        TraditionalChinese = 0x7c03,
        English = 0x0809
    }

    public class ShapeRecognizeResult
    {
        public ShapeRecognizeResult(Point centroid, PointCollection hotPoints, AnalysisAlternate analysisAlternate, InkDrawingNode node)
        {
            Centroid = centroid;
            HotPoints = hotPoints;
            AnalysisAlternate = analysisAlternate;
            InkDrawingNode = node;
        }

        public AnalysisAlternate AnalysisAlternate { get; }

        public Point Centroid { get; set; }

        public PointCollection HotPoints { get; }

        public InkDrawingNode InkDrawingNode { get; }
    }

    /// <summary>
    /// 图形识别类
    /// </summary>
    //public class ShapeRecogniser
    //{
    //    public InkAnalyzer _inkAnalyzer = null;

    //    private ShapeRecogniser()
    //    {
    //        this._inkAnalyzer = new InkAnalyzer
    //        {
    //            AnalysisModes = AnalysisModes.AutomaticReconciliationEnabled
    //        };
    //    }

    //    /// <summary>
    //    /// 根据笔迹集合返回图形名称字符串
    //    /// </summary>
    //    /// <param name="strokeCollection"></param>
    //    /// <returns></returns>
    //    public InkDrawingNode Recognition(StrokeCollection strokeCollection)
    //    {
    //        if (strokeCollection == null)
    //        {
    //            //MessageBox.Show("dddddd");
    //            return null;
    //        }

    //        InkDrawingNode result = null;
    //        try
    //        {
    //            this._inkAnalyzer.AddStrokes(strokeCollection);
    //            if (this._inkAnalyzer.Analyze().Successful)
    //            {
    //                result = _internalAnalyzer(this._inkAnalyzer);
    //                this._inkAnalyzer.RemoveStrokes(strokeCollection);
    //            }
    //        }
    //        catch (System.Exception ex)
    //        {
    //            //result = ex.Message;
    //            System.Diagnostics.Debug.WriteLine(ex.Message);
    //        }

    //        return result;
    //    }

    //    /// <summary>
    //    /// 实现笔迹的分析，返回图形对应的字符串
    //    /// 你在实际的应用中根据返回的字符串来生成对应的Shape
    //    /// </summary>
    //    /// <param name="ink"></param>
    //    /// <returns></returns>
    //    private InkDrawingNode _internalAnalyzer(InkAnalyzer ink)
    //    {
    //        try
    //        {
    //            ContextNodeCollection nodecollections = ink.FindNodesOfType(ContextNodeType.InkDrawing);
    //            foreach (ContextNode node in nodecollections)
    //            {
    //                InkDrawingNode drawingNode = node as InkDrawingNode;
    //                if (drawingNode != null)
    //                {
    //                    return drawingNode;//.GetShapeName();
    //                }
    //            }
    //        }
    //        catch (System.Exception ex)
    //        {
    //            System.Diagnostics.Debug.WriteLine(ex.Message);
    //        }

    //        return null;
    //    }


    //    private static ShapeRecogniser instance = null;
    //    public static ShapeRecogniser Instance
    //    {
    //        get
    //        {
    //            return instance == null ? (instance = new ShapeRecogniser()) : instance;
    //        }
    //    }
    //}


    //用于自动控制其他形状相对于圆的位置

    public class Circle
    {
        public Circle(Point centroid, double r, Stroke stroke)
        {
            Centroid = centroid;
            R = r;
            Stroke = stroke;
        }

        public Point Centroid { get; set; }

        public double R { get; set; }

        public Stroke Stroke { get; set; }
    }
}
