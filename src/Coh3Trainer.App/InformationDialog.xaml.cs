using System.Windows;
using System.Windows.Interop;
using System.Globalization;
using Coh3Trainer.Interop;

namespace Coh3Trainer;

public partial class InformationDialog : Window
{
    public InformationDialog(string section, string heading, string body)
    {
        InitializeComponent();
        SectionText.Text = section.ToUpper(CultureInfo.CurrentUICulture);
        HeadingText.Text = heading;
        BodyText.Text = body;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
