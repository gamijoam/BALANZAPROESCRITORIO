using Pos.Infrastructure.Licensing;

namespace Pos.Integration.Tests;

public sealed class PosLicenseClockGuardTests
{
    [Fact]
    public void Clock_guard_persists_high_watermark_and_detects_rollback_after_restart()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-clock-");
        try
        {
            var protector = new TestProtector();
            var start = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            var first = new PosLicenseClockGuard(directory.FullName, protector);
            Assert.Null(first.CheckAndRecord(start));
            Assert.Null(first.CheckAndRecord(start.AddMinutes(6)));

            var restarted = new PosLicenseClockGuard(directory.FullName, protector);

            Assert.NotNull(restarted.CheckAndRecord(start));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clock_guard_tolerates_small_clock_drift_but_does_not_lower_the_saved_time()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-clock-");
        try
        {
            var protector = new TestProtector();
            var start = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            var guard = new PosLicenseClockGuard(directory.FullName, protector);
            Assert.Null(guard.CheckAndRecord(start));

            Assert.Null(guard.CheckAndRecord(start.AddMinutes(-4)));
            Assert.NotNull(guard.CheckAndRecord(start.AddMinutes(-6)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clock_guard_fails_closed_when_the_protected_marker_cannot_be_read()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-clock-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            var initialGuard = new PosLicenseClockGuard(directory.FullName, new TestProtector());
            Assert.Null(initialGuard.CheckAndRecord(timestamp));
            var corruptedGuard = new PosLicenseClockGuard(directory.FullName, new FailingProtector());

            Assert.Contains("No se pudo validar", corruptedGuard.CheckAndRecord(timestamp));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class TestProtector : IPosLicenseClockProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }

    private sealed class FailingProtector : IPosLicenseClockProtector
    {
        public byte[] Protect(byte[] plaintext) => throw new NotSupportedException();
        public byte[] Unprotect(byte[] protectedData) => throw new System.Security.Cryptography.CryptographicException("marker mismatch");
    }
}
