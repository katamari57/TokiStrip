using System.Windows;
using System.Windows.Media;

namespace VisualBarTimer;

public partial class HelpWindow : Window
{
    public HelpWindow(ImageSource picture)
    {
        InitializeComponent();
        TimerPicture.Source = picture;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
