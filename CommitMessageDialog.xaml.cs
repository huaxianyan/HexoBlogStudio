using System.Windows;
using HexoBlogStudio.Services;

namespace HexoBlogStudio;

public partial class CommitMessageDialog : Window
{
    public string Message { get; private set; } = string.Empty;

    public CommitMessageDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => MessageInput.Focus();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var message = GitPublisher.SanitizeCommitMessage(MessageInput.Text);
        if (message.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "写一句提交说明再发布。", "说明是空的",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Message = message;
        DialogResult = true;
    }
}
