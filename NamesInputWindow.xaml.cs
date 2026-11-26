using System;
using System.IO;
using System.Windows;
using Painter.Helpers;

namespace Painter
{
    /// <summary>
    /// Interaction logic for NamesInputWindow.xaml
    /// </summary>
    public partial class NamesInputWindow : Window
    {
        public NamesInputWindow()
        {
            InitializeComponent();
            WindowBackdrop.Apply(this);
        }

        string originText = "";

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(App.RootPath + "Names.txt"))
                {
                    TextBoxNames.Text = File.ReadAllText(App.RootPath + "Names.txt");
                    originText = TextBoxNames.Text;
                }
            }
            catch
            {
                // 文件被占用或读取失败时保持空名单可编辑，不让异常冲到 UI 事件循环
                originText = TextBoxNames.Text;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (originText != TextBoxNames.Text)
            {
                var result = MessageBox.Show("是否保存？", "名单导入", MessageBoxButton.YesNo);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.WriteAllText(App.RootPath + "Names.txt", TextBoxNames.Text);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("保存名单失败：" + ex.Message, "名单导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
