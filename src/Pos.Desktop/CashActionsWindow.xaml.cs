using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CashActionsWindow : Window
{
    public bool CloseCashRequested { get; private set; }

    public CashActionsWindow(CashSessionInfo session)
    {
        InitializeComponent();
        SessionInfo.Text = $"Turno {session.Currency} abierto el {session.OpenedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}. Fondo inicial: {PosNumberFormat.FormatAmount(session.OpeningBalance)} {session.Currency}.";
    }

    private void Movement_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CloseCash_Click(object sender, RoutedEventArgs e) { CloseCashRequested = true; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
