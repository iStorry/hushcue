using Hushcue.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Hushcue;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TitleText.Text = Product.Name;
        TaglineText.Text = Product.Tagline;
        AppWindow.Resize(new SizeInt32(1000, 700));
    }
}
