namespace CoreLib
{
    internal sealed class KeptAliveByExcludedCode
    {
        public static int MeaningOfLife() => 42;
    }
}

namespace Excluded.Fake
{
    public sealed class ExcludedDeadCode
    {
        private int _excludedUnusedField = 1;

        public int CallSite()
        {
            return ExcludedUnusedMethod(1);
        }

        private int ExcludedUnusedMethod(int excludedUnusedParam)
        {
            var excludedUnusedLocal = 5;
            return 0;
        }
    }

    internal sealed class ExcludedInternalType
    {
        public void ExcludedInternalMember() { }
    }

    public sealed class ExcludedOptions
    {
        public string NeverConfigured { get; set; } = string.Empty;
    }

    public static class ExcludedConsumer
    {
        public static int Consume() => CoreLib.KeptAliveByExcludedCode.MeaningOfLife();
    }

    public sealed class ExcludedLegacy
    {
        [System.Obsolete]
        private void ExcludedOldMethod() { }
    }
}
