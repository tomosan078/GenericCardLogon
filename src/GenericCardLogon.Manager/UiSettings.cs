using Microsoft.Win32;
using System;

namespace GenericCardLogon.Manager
{
    internal static class UiSettings
    {
        private const string KeyPath = @"SOFTWARE\GenericCardLogon";
        public const string DefaultProviderLabel = "GenericCardLogon";
        public const string DefaultInstruction = "ICカードをかざしてください";

        public static string ProviderLabel
        {
            get { return Read("ProviderLabel", DefaultProviderLabel); }
        }

        public static string Instruction
        {
            get { return Read("Instruction", DefaultInstruction); }
        }

        public static void Save(string providerLabel, string instruction)
        {
            providerLabel = string.IsNullOrWhiteSpace(providerLabel) ? DefaultProviderLabel : providerLabel.Trim();
            instruction = string.IsNullOrWhiteSpace(instruction) ? DefaultInstruction : instruction.Trim();
            using (var key = Registry.LocalMachine.CreateSubKey(KeyPath))
            {
                if (key == null) throw new InvalidOperationException("設定レジストリを開けませんでした。");
                key.SetValue("ProviderLabel", providerLabel, RegistryValueKind.String);
                key.SetValue("Instruction", instruction, RegistryValueKind.String);
            }
        }

        private static string Read(string name, string fallback)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(KeyPath, false))
                {
                    var value = key == null ? null : key.GetValue(name) as string;
                    return string.IsNullOrWhiteSpace(value) ? fallback : value;
                }
            }
            catch { return fallback; }
        }
    }
}
