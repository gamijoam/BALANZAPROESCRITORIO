using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Pos.Server;

internal static partial class PosLicenseRequestGate
{
    [GeneratedRegex("^/api/v1/cash-sessions/[0-9a-fA-F-]{36}/close$", RegexOptions.CultureInvariant)]
    private static partial Regex CashSessionClosePath();

    [GeneratedRegex("^/api/v1/sales/[0-9a-fA-F-]{36}/returns$", RegexOptions.CultureInvariant)]
    private static partial Regex SaleReturnPath();

    [GeneratedRegex("^/api/v1/customers/[0-9a-fA-F-]{36}/payments$", RegexOptions.CultureInvariant)]
    private static partial Regex ReceivablePaymentPath();

    [GeneratedRegex("^/api/v1/terminals/[0-9a-fA-F-]{36}/unpair$", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalUnpairPath();

    public static bool IsAllowedWhileLocked(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            return true;
        if (!HttpMethods.IsPost(request.Method))
            return false;

        var path = request.Path.Value ?? string.Empty;
        return path is "/api/v1/auth/login" or "/api/v1/auth/logout" or "/api/v1/auth/authorize" or
            "/api/v1/setup/first-operator" or "/api/v1/license/import" or "/api/v1/sales" ||
            CashSessionClosePath().IsMatch(path) || SaleReturnPath().IsMatch(path) || ReceivablePaymentPath().IsMatch(path) || TerminalUnpairPath().IsMatch(path);
    }
}
