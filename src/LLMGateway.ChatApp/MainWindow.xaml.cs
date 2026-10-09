using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace LLMGateway.ChatApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Action<string> _recordLifecycle;
    private bool _closeRequested;
    private bool _closeCompleted;
    private bool _shutdownFailed;

    public MainWindow() : this(new MainViewModel(), GatewayUiDiagnostics.Record)
    {
    }

    internal MainWindow(MainViewModel viewModel, Action<string> recordLifecycle, ResourceDictionary? resources = null)
    {
        _viewModel = viewModel;
        _recordLifecycle = recordLifecycle;
        try
        {
            if (resources is not null) Resources = resources;
            _recordLifecycle("MainWindowStarting");
            InitializeComponent();
            _recordLifecycle("MainWindowInitialized");
            DataContext = _viewModel;
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            Loaded += (_, _) =>
            {
                _recordLifecycle("MainWindowLoaded");
                // _viewModel.ConnectCommand.Execute(null);
            };
            Closed += (_, _) =>
            {
                _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
                _recordLifecycle("MainWindowClosed");
            };
        }
        catch (Exception)
        {
            _recordLifecycle("MainWindowInitializationFailed");
            throw;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
    }

    private void OnAccountDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.EditCommand.Execute(null);

    protected override async void OnClosing(CancelEventArgs e)
    {
        _recordLifecycle("MainWindowClosing");
        base.OnClosing(e);
        if (_closeCompleted) return;
        if (_shutdownFailed)
        {
            // This explicit repeat closes the UI without claiming that failed cleanup succeeded.
            _recordLifecycle("MainWindowClosingAfterShutdownFailure");
            return;
        }
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception)
        {
            _recordLifecycle("MainWindowShutdownFailed");
            _shutdownFailed = true;
            Title = "LLMGateway — завершение не подтверждено; закройте окно ещё раз, чтобы выйти";
            _closeRequested = false;
            return;
        }
        _closeCompleted = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
