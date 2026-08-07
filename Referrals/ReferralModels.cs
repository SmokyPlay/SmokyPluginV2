namespace SmokyPluginV2.Referrals
{
    using System;
    using System.Collections.Generic;

    internal sealed class ReferralStatus
    {
        public string PlayerUserId { get; set; }

        public string ReferralCode { get; set; }

        public bool HasReferralPrivilege { get; set; }

        public IReadOnlyList<ReferralParticipant> Participants { get; set; } =
            Array.Empty<ReferralParticipant>();
    }

    internal sealed class ReferralParticipant
    {
        public string PlayerUserId { get; set; }

        public string Nickname { get; set; }

        public long TotalPlaytimeSeconds { get; set; }

        public DateTime AcceptedAtUtc { get; set; }
    }

    internal sealed class ReferralAccessState
    {
        public int QualifiedReferralCount { get; set; }

        public bool IsPendingInvitee { get; set; }
    }

    internal sealed class ReferralQualificationTransition
    {
        public string InviterPlayerUserId { get; set; }

        public bool RewardThresholdReached { get; set; }

        public bool InviteeQualified { get; set; }

        public bool InviteeJustQualified { get; set; }
    }
}
