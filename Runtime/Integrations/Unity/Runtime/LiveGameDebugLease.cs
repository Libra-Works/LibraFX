namespace LiberaFx.Transport
{
    // Transport-only ownership, not a credential. A late disconnect from an old
    // window must never clear a newer window's debug transfer.
    public sealed class LiveGameDebugLease
    {
        private readonly string session;
        public string Owner { get; private set; } = string.Empty;
        public bool OwnerRequired { get; private set; }
        public LiveGameDebugLease(string session)
        {
            if (!Valid(session)) throw new System.ArgumentException("Invalid endpoint session.");
            this.session = session;
        }
        public static bool Valid(string token)
        {
            if (token == null || token.Length != 32) return false;
            foreach (var character in token)
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) return false;
            return true;
        }
        public bool Attach(string endpointSession, string owner)
        {
            if (endpointSession != session || !Valid(owner)) return false;
            Owner = owner;
            OwnerRequired = true;
            return true;
        }
        public bool Matches(string endpointSession, string owner) =>
            endpointSession == session && Valid(owner) && Owner == owner;
        public bool AllowsMutation(string endpointSession, string owner) =>
            endpointSession == session && ((!OwnerRequired && string.IsNullOrEmpty(owner)) || Matches(endpointSession, owner));
        public bool Detach(string endpointSession, string owner)
        {
            if (!Matches(endpointSession, owner)) return false;
            Owner = string.Empty;
            return true;
        }
    }
}
