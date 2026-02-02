using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Fish_DeObfuscator.core.Utils;
using Fish.Shared;
using Microsoft.Win32;

namespace Fish_DeObfuscator.UI
{
    public partial class MainWindow : Window
    {
        private string _selectedFilePath;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = ".NET Assemblies|*.dll;*.exe|All Files|*.*", Title = "Select a .NET Assembly" };

            if (dialog.ShowDialog() == true)
            {
                SetSelectedFile(dialog.FileName);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    SetSelectedFile(files[0]);
                }
            }
        }

        private void SetSelectedFile(string filePath)
        {
            _selectedFilePath = filePath;
            FilePathTextBox.Text = filePath;
            DeobfuscateButton.IsEnabled = true;
        }

        private async void DeobfuscateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
            {
                MessageBox.Show("Please select a valid file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetUIBusy(true);

            try
            {
                string result = await Task.Run(() => RunDeobfuscation());
                StatusText.Text = "Completed";
                StatusText.Foreground = (Brush)FindResource("SuccessBrush");
                MessageBox.Show(result, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed";
                StatusText.Foreground = (Brush)FindResource("ErrorBrush");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIBusy(false);
            }
        }

        private string RunDeobfuscation()
        {
            var options = BuildOptions();
            var context = new UIContext(options, null);

            if (!context.IsInitialized())
            {
                throw new Exception("Failed to load assembly.");
            }

            if (context.Options.Stages.Count == 0)
            {
                context.ModuleDefinition?.Dispose();
                throw new Exception("No deobfuscation stages selected.");
            }

            foreach (IStage stage in context.Options.Stages)
            {
                stage.Execute(context);
            }

            try
            {
                context.SaveContext();
                return $"Deobfuscation completed!\nOutput: {context.Options.AssemblyOutput}";
            }
            finally
            {
                context.ModuleDefinition?.Dispose();
            }
        }

        private UIOptions BuildOptions()
        {
            var stages = new System.Collections.Generic.List<string>();

            Dispatcher.Invoke(() =>
            {
                if (StringCheckBox.IsChecked == true)
                    stages.Add("string");
                if (VirtualizationCheckBox.IsChecked == true)
                    stages.Add("virtualization");
                if (CalliCheckBox.IsChecked == true)
                    stages.Add("calli");
                if (ControlFlowCheckBox.IsChecked == true)
                    stages.Add("controlflow");
                if (LocalCleanerCheckBox.IsChecked == true)
                    stages.Add("localcleaner");
            });

            return new UIOptions(_selectedFilePath, stages);
        }

        private void SetUIBusy(bool busy)
        {
            DeobfuscateButton.IsEnabled = !busy;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = busy;
            StatusText.Text = busy ? "Processing..." : "Ready";
            StatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }

        private void ObfuscatorType_Changed(object sender, RoutedEventArgs e) { }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            StringCheckBox.IsChecked = true;
            VirtualizationCheckBox.IsChecked = true;
            CalliCheckBox.IsChecked = true;
            ControlFlowCheckBox.IsChecked = true;
            LocalCleanerCheckBox.IsChecked = true;
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            StringCheckBox.IsChecked = false;
            VirtualizationCheckBox.IsChecked = false;
            CalliCheckBox.IsChecked = false;
            ControlFlowCheckBox.IsChecked = false;
            LocalCleanerCheckBox.IsChecked = false;
        }
    }
}
