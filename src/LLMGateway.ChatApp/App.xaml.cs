using System.Windows;
using System.Windows.Threading;

namespace LLMGateway.ChatApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        GatewayUiDiagnostics.Record("Startup");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                GatewayUiDiagnostics.Record($"AppDomainUnhandledException:{ex.GetType().Name}");
            else
                GatewayUiDiagnostics.Record("AppDomainUnhandledException");
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GatewayUiDiagnostics.Record("Exit");
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        GatewayUiDiagnostics.Record($"DispatcherUnhandledException:{e.Exception.GetType().Name}");
        MessageBox.Show(GatewayUiDiagnostics.Describe(e.Exception), "LLMGateway", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
