using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Ink;
using System.Windows.Input;

namespace Painter.Helpers
{
    public class TimeMachine
    {
        private readonly List<TimeMachineHistory> _currentStrokeHistory = new List<TimeMachineHistory>();

        private int _currentIndex = -1;
        private const int MaxHistoryCount = 200; // 限制历史记录数量，防止内存无限增长

        public delegate void OnUndoStateChange(bool status);

        public event OnUndoStateChange OnUndoStateChanged;

        public delegate void OnRedoStateChange(bool status);

        public event OnRedoStateChange OnRedoStateChanged;

        private void TrimHistory()
        {
            if (_currentStrokeHistory.Count > MaxHistoryCount)
            {
                int removeCount = _currentStrokeHistory.Count - MaxHistoryCount;
                _currentStrokeHistory.RemoveRange(0, removeCount);
                _currentIndex -= removeCount;
                if (_currentIndex < -1) _currentIndex = -1;
            }
        }

        public void CommitStrokeUserInputHistory(StrokeCollection stroke)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(new TimeMachineHistory(stroke, TimeMachineHistoryType.UserInput, false));
            _currentIndex = _currentStrokeHistory.Count - 1;
            TrimHistory();
            NotifyUndoRedoState();
        }

        public void CommitStrokeShapeHistory(StrokeCollection strokeToBeReplaced, StrokeCollection generatedStroke)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(new TimeMachineHistory(generatedStroke,
                TimeMachineHistoryType.ShapeRecognition,
                false,
                strokeToBeReplaced));
            _currentIndex = _currentStrokeHistory.Count - 1;
            TrimHistory();
            NotifyUndoRedoState();
        }

        public void CommitStrokeManipulationHistory(Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> stylusPointDictionary)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(
                new TimeMachineHistory(stylusPointDictionary,
                    TimeMachineHistoryType.Manipulation));
            _currentIndex = _currentStrokeHistory.Count - 1;
            TrimHistory();
            NotifyUndoRedoState();
        }
        public void CommitStrokeDrawingAttributesHistory(Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> drawingAttributes)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(
                new TimeMachineHistory(drawingAttributes,
                    TimeMachineHistoryType.DrawingAttributes));
            _currentIndex = _currentStrokeHistory.Count - 1;
            TrimHistory();
            NotifyUndoRedoState();
        }
        public void CommitStrokeEraseHistory(StrokeCollection stroke, StrokeCollection sourceStroke = null)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(new TimeMachineHistory(stroke, TimeMachineHistoryType.Clear, true, sourceStroke));
            _currentIndex = _currentStrokeHistory.Count - 1;
            TrimHistory();
            NotifyUndoRedoState();
        }

        public void ClearStrokeHistory()
        {
            _currentStrokeHistory.Clear();
            _currentIndex = -1;
            NotifyUndoRedoState();
        }

        public TimeMachineHistory Undo()
        {
            if (_currentIndex < 0) return null;
            var item = _currentStrokeHistory[_currentIndex];
            item.StrokeHasBeenCleared = !item.StrokeHasBeenCleared;
            _currentIndex--;
            OnUndoStateChanged?.Invoke(_currentIndex > -1);
            OnRedoStateChanged?.Invoke(_currentStrokeHistory.Count - _currentIndex - 1 > 0);
            return item;
        }

        public TimeMachineHistory Redo()
        {
            if (_currentIndex + 1 >= _currentStrokeHistory.Count) return null;
            var item = _currentStrokeHistory[++_currentIndex];
            item.StrokeHasBeenCleared = !item.StrokeHasBeenCleared;
            NotifyUndoRedoState();
            return item;
        }

        public TimeMachineHistory[] ExportTimeMachineHistory()
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            return _currentStrokeHistory.ToArray();
        }

        public bool ImportTimeMachineHistory(TimeMachineHistory[] sourceHistory)
        {
            _currentStrokeHistory.Clear();
            _currentStrokeHistory.AddRange(sourceHistory);
            _currentIndex = _currentStrokeHistory.Count - 1;
            NotifyUndoRedoState();
            return true;
        }
        private void NotifyUndoRedoState()
        {
            OnUndoStateChanged?.Invoke(_currentIndex > -1);
            OnRedoStateChanged?.Invoke(_currentStrokeHistory.Count - _currentIndex - 1 > 0);
        }
    }

    public class TimeMachineHistory
    {
        public TimeMachineHistoryType CommitType;
        public bool StrokeHasBeenCleared = false;
        // 存储 ISF 字节流而非 StrokeCollection，节省内存、加快序列化
        public byte[] CurrentStrokeData;
        public byte[] ReplacedStrokeData;
        public Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> StylusPointDictionary;
        public Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> DrawingAttributes;

        public TimeMachineHistory(StrokeCollection currentStroke, TimeMachineHistoryType commitType, bool strokeHasBeenCleared)
        {
            CommitType = commitType;
            CurrentStrokeData = StrokeCollectionToBytes(currentStroke);
            StrokeHasBeenCleared = strokeHasBeenCleared;
            ReplacedStrokeData = null;
        }
        public TimeMachineHistory(StrokeCollection currentStroke, TimeMachineHistoryType commitType, bool strokeHasBeenCleared, StrokeCollection replacedStroke)
        {
            CommitType = commitType;
            CurrentStrokeData = StrokeCollectionToBytes(currentStroke);
            StrokeHasBeenCleared = strokeHasBeenCleared;
            ReplacedStrokeData = StrokeCollectionToBytes(replacedStroke);
        }
        public TimeMachineHistory(Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> stylusPointDictionary, TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            StylusPointDictionary = stylusPointDictionary;
        }
        public TimeMachineHistory(Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> drawingAttributes, TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            DrawingAttributes = drawingAttributes;
        }

        public StrokeCollection GetCurrentStroke()
        {
            return CurrentStrokeData != null && CurrentStrokeData.Length > 0
                ? BytesToStrokeCollection(CurrentStrokeData)
                : new StrokeCollection();
        }

        public StrokeCollection GetReplacedStroke()
        {
            return ReplacedStrokeData != null && ReplacedStrokeData.Length > 0
                ? BytesToStrokeCollection(ReplacedStrokeData)
                : new StrokeCollection();
        }

        private static byte[] StrokeCollectionToBytes(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0) return Array.Empty<byte>();
            using (var ms = new MemoryStream())
            {
                strokes.Save(ms);
                return ms.ToArray();
            }
        }

        private static StrokeCollection BytesToStrokeCollection(byte[] data)
        {
            if (data == null || data.Length == 0) return new StrokeCollection();
            using (var ms = new MemoryStream(data))
            {
                return new StrokeCollection(ms);
            }
        }
    }

    public enum TimeMachineHistoryType
    {
        UserInput,
        ShapeRecognition,
        Clear,
        Manipulation,
        DrawingAttributes
    }
}