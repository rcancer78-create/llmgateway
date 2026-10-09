using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Xml.Linq;
namespace LLMGateway.ChatApp.Tests;
public sealed class ChatAppTemplateReviewTests
{
    [Fact]
    public Task TextInputHonorsRequestedScrolling() => OnSta(() =>
    {
        var resources = Resources();
        var box = new TextBox { Style = (Style)resources[typeof(TextBox)], Text = new string('x', 1000),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.ApplyTemplate(); box.Measure(new Size(200, 100)); box.Arrange(new Rect(0, 0, 200, 100));
        var host = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        Assert.Equal(ScrollBarVisibility.Auto, host.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, host.VerticalScrollBarVisibility);
        box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        Assert.Equal(ScrollBarVisibility.Disabled, host.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Visible, host.VerticalScrollBarVisibility);
    });
    [Theory]
    [InlineData(Orientation.Horizontal)]
    [InlineData(Orientation.Vertical)]
    public Task ScrollThumbFollowsTheActualBarOrientation(Orientation orientation) => OnSta(() =>
    {
        var resources = Resources();
        var bar = new ScrollBar { Style = (Style)resources[typeof(ScrollBar)], Orientation = orientation, Maximum = 100, Value = 25 };
        bar.ApplyTemplate(); bar.Measure(new Size(200, 200)); bar.Arrange(new Rect(0, 0, 200, 200));
        var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
        Assert.Equal(orientation, track.Orientation);
        Assert.Equal(25, track.Value);
    });
    [Fact]
    public Task ReasoningHeaderAndExpandedBodyRemainDistinct() => OnSta(() =>
    {
        var resources = Resources();
        var content = new TextBlock { Text = "owned reasoning body" };
        var expander = new Expander { Style = (Style)resources[typeof(Expander)], Header = "owned header", Content = content, IsExpanded = true };
        expander.ApplyTemplate(); expander.Measure(new Size(500, 300)); expander.Arrange(new Rect(0, 0, 500, 300));
        var header = Assert.IsType<ToggleButton>(expander.Template.FindName("HeaderSite", expander));
        var headerPresenter = Assert.IsType<ContentPresenter>(header.Content);
        Assert.Equal("owned header", headerPresenter.Content);
        var body = Assert.IsType<ContentPresenter>(expander.Template.FindName("ExpandSite", expander));
        Assert.Same(content, body.Content); Assert.Equal(Visibility.Visible, body.Visibility);
    });
    private static ResourceDictionary Resources()
    {
        var styles = Read("Styles"); var root = new XElement(styles.Root!);
        root.AddFirst(Read("Colors").Root!.Elements().Concat(Read("Icons").Root!.Elements()).Select(x => new XElement(x)));
        return (ResourceDictionary)XamlReader.Parse(root.ToString());
        static XDocument Read(string name)
        {
            using var stream = typeof(ChatAppTemplateReviewTests).Assembly.GetManifestResourceStream($"OwnedStyles.{name}.xaml")!;
            return XDocument.Load(stream);
        }
    }
    private static Task OnSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completed.SetResult(); } catch (Exception e) { completed.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completed.Task;
    }
}
