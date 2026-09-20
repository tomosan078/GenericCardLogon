using System.Windows;
using System.Security.Principal;

namespace GenericCardLogon.Manager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show("このアプリケーションは管理者権限で実行する必要があります。", "GenericCardLogon", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
            }
        }
    }
}
