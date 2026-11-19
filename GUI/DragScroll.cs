using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fwoign
{
    /// <summary>
    /// 触摸屏「按住拖动 = 滚动」附加行为。
    ///
    /// 背景：现场使用的是 USB 触摸屏（触摸控制板把触摸模拟成鼠标），
    /// WPF 收不到真正的 Touch / Manipulation 事件，所以 ScrollViewer.PanningMode 无效——
    /// 单指拖动只会变成「拖选文字」。这里改成用鼠标事件自己实现拖动滚动。
    ///
    /// 用法：DragScroll.Enabled="True"（可加在一行 RichTextBox / ListView 上）。
    /// 代价：这两个控件里按住拖动不再用于选择文字/拖动条目，单击仍照常。
    /// </summary>
    public static class DragScroll
    {
        /// <summary>起拖阈值（像素）：小于这个位移仍视为点击，避免误判。</summary>
        private const double Threshold = 6.0;

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled", typeof(bool), typeof(DragScroll),
                new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject element, bool value)
        {
            element.SetValue(EnabledProperty, value);
        }

        public static bool GetEnabled(DependencyObject element)
        {
            return (bool)element.GetValue(EnabledProperty);
        }

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            FrameworkElement fe = d as FrameworkElement;
            if (fe == null) return;

            if ((bool)e.NewValue)
            {
                fe.Loaded += OnLoaded;
                fe.Unloaded += OnUnloaded;
                if (fe.IsLoaded) Attach(fe);
            }
            else
            {
                fe.Loaded -= OnLoaded;
                fe.Unloaded -= OnUnloaded;
                Detach(fe);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach(sender as FrameworkElement);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach(sender as FrameworkElement);
        }

        private class State
        {
            public ScrollViewer Scroller;
            public Point Start;
            public bool Pressed;
            public bool Dragging;
        }

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached("State", typeof(State), typeof(DragScroll), new PropertyMetadata(null));

        private static void Attach(FrameworkElement fe)
        {
            if (fe == null) return;
            if (fe.GetValue(StateProperty) != null) return;

            ScrollViewer sv = FindScrollViewer(fe);
            if (sv == null) return;   // 模板还没生成，等下次 Loaded

            var st = new State { Scroller = sv };
            fe.SetValue(StateProperty, st);

            fe.PreviewMouseLeftButtonDown += OnDown;
            fe.PreviewMouseMove += OnMove;
            fe.PreviewMouseLeftButtonUp += OnUp;
            fe.LostMouseCapture += OnLostCapture;
            fe.PreviewMouseWheel += OnWheel;
        }

        private static void Detach(FrameworkElement fe)
        {
            if (fe == null) return;
            var st = fe.GetValue(StateProperty) as State;
            if (st == null) return;

            fe.ClearValue(StateProperty);
            fe.PreviewMouseLeftButtonDown -= OnDown;
            fe.PreviewMouseMove -= OnMove;
            fe.PreviewMouseLeftButtonUp -= OnUp;
            fe.LostMouseCapture -= OnLostCapture;
            fe.PreviewMouseWheel -= OnWheel;
        }

        private static void OnDown(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var st = fe == null ? null : fe.GetValue(StateProperty) as State;
            if (st == null) return;

            st.Start = e.GetPosition(fe);
            st.Pressed = true;
            st.Dragging = false;
        }

        private static void OnMove(object sender, MouseEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var st = fe == null ? null : fe.GetValue(StateProperty) as State;
            if (st == null || !st.Pressed) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                st.Pressed = false;
                st.Dragging = false;
                return;
            }

            Point p = e.GetPosition(fe);
            double dy = st.Start.Y - p.Y;

            if (!st.Dragging)
            {
                if (Math.Abs(dy) < Threshold) return;
                st.Dragging = true;
                try { fe.CaptureMouse(); } catch { }
            }

            // 手指往上拖 = 内容跟着往上走
            double target = st.Scroller.VerticalOffset + dy;
            if (target < 0) target = 0;
            double max = st.Scroller.ScrollableHeight;
            if (target > max) target = max;
            st.Scroller.ScrollToVerticalOffset(target);

            st.Start = p;
            e.Handled = true;      // 阻止 RichTextBox 继续扩选文字
        }

        private static void OnUp(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var st = fe == null ? null : fe.GetValue(StateProperty) as State;
            if (st == null) return;

            if (st.Dragging)
            {
                try { fe.ReleaseMouseCapture(); } catch { }
                e.Handled = true;  // 拖完不要再当成一次点击
            }
            st.Pressed = false;
            st.Dragging = false;
        }

        private static void OnLostCapture(object sender, MouseEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var st = fe == null ? null : fe.GetValue(StateProperty) as State;
            if (st == null) return;
            st.Pressed = false;
            st.Dragging = false;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var st = fe == null ? null : fe.GetValue(StateProperty) as State;
            if (st == null) return;

            st.Scroller.ScrollToVerticalOffset(st.Scroller.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        /// <summary>在可视树里找第一个 ScrollViewer（RichTextBox / ListView 的模板内都有）。</summary>
        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            ScrollViewer sv = root as ScrollViewer;
            if (sv != null) return sv;

            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
    }
}
