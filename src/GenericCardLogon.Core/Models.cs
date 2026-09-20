using System;

namespace GenericCardLogon.Core
{
    public sealed class CardRegistration
    {
        public string CardType { get; set; }
        public string IdmHash { get; set; }
        public string UserName { get; set; }
        public string PasswordProtectedBase64 { get; set; }
        public DateTime RegisteredAtUtc { get; set; }
    }

    public sealed class PipeMessage
    {
        public string Type { get; set; }
        public string IdmHash { get; set; }
        public string UserName { get; set; }
    }
}
