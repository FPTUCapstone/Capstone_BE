using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Infrastructure.Reviews.Moderation;

internal static partial class DeterministicLocalReviewPolicyEvaluator
{
    private static readonly HashSet<string> EnglishFunctionWords = new(StringComparer.Ordinal)
    {
        "a", "am", "an", "and", "are", "at", "be", "because", "before", "but", "can",
        "cannot", "could", "did", "do", "does", "for", "from", "had", "has", "have", "he", "her",
        "him", "i", "in", "is", "it", "may", "me", "must", "my", "not", "of", "on",
        "our", "she", "should", "so", "that", "the", "they", "this", "to", "too", "us",
        "very", "was", "we", "were", "while", "will", "with", "would", "you",
    };

    private static readonly HashSet<string> EnglishVocabulary = new(
        EnglishFunctionWords.Concat(
        [
            "about", "address", "advertised", "amazing", "airport", "awful", "beautiful", "bus",
            "clean", "clothes", "comment", "complaint", "dirty", "disappointing",
            "driver", "enjoyable", "experience", "felt", "food", "friendly", "good",
            "great", "guide", "harass", "helpful", "hotel", "hurt", "ignore", "instructions",
            "everyone", "everybody", "someone", "somebody", "should", "kill", "murder",
            "must", "need", "ought", "want", "have", "had", "no", "never", "plan",
            "plans", "planned", "intend", "intended", "intention", "intentions",
            "harmed", "killed", "murdered",
            "beaten", "attacked", "stabbed", "shot", "punched", "poisoned",
            "strangled", "burned", "burnt", "assaulted",
            "die", "dead", "death", "disgusting", "subhuman", "scum", "black",
            "banned", "oral", "sex", "masturbation", "cryptocurrency", "wallet",
            "double", "cell", "number", "post", "written", "wrote", "message", "planning",
            "chess", "game", "cards", "competition", "race", "destination", "meeting",
            "point", "shoot", "email", "text", "note", "time", "heat", "photos",
            "pictures", "photographs", "waiting", "copy", "itinerary", "kindness",
            "traffic", "record", "video", "breeze", "calories", "hike", "punch",
            "tickets", "lights", "boredom", "steep", "trail", "tomorrow", "disc",
            "praise", "travel", "tip", "join", "again",
            "serve", "gay", "black", "muslim", "disabled", "ban", "banned",
            "exclude", "excluded", "remove", "removed", "allowed", "duplicate",
            "bookings", "waiting", "list", "tourists", "travelers", "here", "main",
            "street", "go", "abuse", "pest", "removal", "cleaning", "cleaned", "pool",
            "vermin", "human", "garbage", "beach", "ordinary", "url", "details",
            "example", "com", "kind", "littering", "up", "hell", "straight", "right",
            "meant", "prepared", "served", "intended",
            "knee", "late", "money", "moderation", "people", "poor", "private", "room",
            "prize", "refund", "reporting", "review", "rude", "rushed", "scam",
            "schedule", "scenery", "service", "staff", "star", "steps", "terrible", "tour",
            "threat", "trip", "unsafe", "warned", "worst",
        ]),
        StringComparer.Ordinal);

    private static readonly HashSet<string> VietnameseVocabulary = new(StringComparer.Ordinal)
    {
        "an", "bao", "bi", "canh", "cham", "chan", "cho", "chuyen", "co", "cua",
        "da", "dan", "dat", "den", "dep", "di", "dich", "diem", "do", "dung",
        "gia", "giai", "gap", "hay", "hoan", "hoi", "huong", "khong", "la", "lai",
        "lich", "lo", "minh", "mot", "muon", "nay", "nghi", "nghiem", "nguoi",
        "nhung", "noi", "o", "phong", "pho", "qua", "rat", "sao", "se", "sach",
        "tai", "te", "than", "thich", "thien", "tho", "tien", "toi", "tour",
        "trai", "trinh", "tuyet", "va", "vi", "vien", "voi", "vu", "xe", "xu",
        "yeu", "hai", "long", "chat", "luong", "xuat", "sac", "ai", "giet",
        "moi", "nen", "nhan", "ban", "can", "y", "dinh", "chet", "he", "vao", "thang",
    };

    private static readonly HashSet<string> VietnameseLanguageSignals = new(StringComparer.Ordinal)
    {
        "bao", "canh", "cham", "chan", "chuyen", "dich", "diem", "dung", "giai",
        "hoan", "hoi", "huong", "khong", "lich", "minh", "muon", "nghi", "nghiem",
        "nguoi", "nhung", "phong", "sach", "sao", "te", "than", "thich", "thien",
        "tho", "tien", "toi", "trai", "trinh", "tuyet", "vien", "voi", "vu", "xe",
        "yeu", "hai", "long", "chat", "luong", "xuat", "sac", "giet", "moi",
        "nen", "nhan", "ban", "can", "y", "dinh", "chet", "he", "vao", "thang",
    };

    private static readonly HashSet<string>[] UnsupportedLatinLanguageMarkers =
    [
        new(StringComparer.Ordinal)
        {
            "agreable", "autobus", "arrive", "etait", "le", "mais", "mauvais",
            "retard", "voyage",
        },
        new(StringComparer.Ordinal)
        {
            "agradable", "autobus", "el", "fue", "llego", "pero", "tarde", "viaje",
        },
        new(StringComparer.Ordinal)
        {
            "der", "die", "das", "reise", "war", "aber", "schlecht",
        },
        new(StringComparer.Ordinal)
        {
            "il", "viaggio", "era", "ma", "brutto",
        },
        new(StringComparer.Ordinal)
        {
            "a", "viagem", "foi", "mas", "ruim",
        },
    ];

    public static ValueTask<ReviewContentModerationResult> EvaluateAsync(
        ReviewText text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        string source = $"{text.Title}\n{text.Content}";
        if (!TryFoldLatinText(source, out _)
            || !TryCanonicalizeSupportedText(text.Content, out _))
        {
            return ValueTask.FromResult(ReviewContentModerationResult.Unavailable());
        }

        if (!TryPrepareClassificationSource(source, out string classificationSource))
        {
            return ValueTask.FromResult(ReviewContentModerationResult.Unavailable());
        }

        string[] canonicalSentences = SentenceBoundaryRegex()
            .Split(classificationSource)
            .Select(segment => SemicolonBoundaryRegex().Replace(segment, " clausebreak "))
            .Select(segment => ColonBoundaryRegex().Replace(segment, " clausebreak "))
            .Select(segment => CommaBoundaryRegex().Replace(segment, " commabreak "))
            .Select(segment => DashBoundaryRegex().Replace(segment, " dashbreak "))
            .Select(segment => BracketBoundaryRegex().Replace(segment, " bracketbreak "))
            .Select(segment => SeparatorBoundaryRegex().Replace(segment, " separatorbreak "))
            .Where(segment => TryFoldLatinText(segment, out _))
            .Select(segment =>
            {
                TryFoldLatinText(segment, out string canonical);
                return canonical;
            })
            .ToArray();

        var categories = new List<ReviewPolicyCategory>(6);

        if (canonicalSentences.Any(IsThreatOrCallForPhysicalHarm))
        {
            categories.Add(ReviewPolicyCategory.ThreatOrCallForPhysicalHarm);
        }

        if (canonicalSentences.Any(IsTargetedDegradingHarassment))
        {
            categories.Add(ReviewPolicyCategory.TargetedDegradingHarassment);
        }

        if (canonicalSentences.Any(IsHateOrExclusion))
        {
            categories.Add(ReviewPolicyCategory.HateOrExclusionBasedOnProtectedCharacteristic);
        }

        if (canonicalSentences.Any(IsUnrelatedExplicitSexualContent))
        {
            categories.Add(ReviewPolicyCategory.UnrelatedExplicitSexualContent);
        }

        if (canonicalSentences.Any(IsTargetedPrivateDisclosureForHarm))
        {
            categories.Add(ReviewPolicyCategory.TargetedPrivateContactOrResidentialDisclosureForHarm);
        }

        if (canonicalSentences.Any(IsUnrelatedAdvertisingSpamScamOrManipulation))
        {
            categories.Add(
                ReviewPolicyCategory.UnrelatedAdvertisingSpamPhishingScamOrFraudulentSolicitation);
        }

        if (categories.Count == 0
            && canonicalSentences.Any(IsAmbiguousPotentialPolicyViolation))
        {
            return ValueTask.FromResult(ReviewContentModerationResult.Unavailable());
        }

        ReviewContentModerationResult result = categories.Count == 0
            ? ReviewContentModerationResult.Accepted(ReviewContentPolicy.ActiveVersion)
            : ReviewContentModerationResult.Rejected([.. categories]);
        return ValueTask.FromResult(result);
    }

    private static bool IsThreatOrCallForPhysicalHarm(string text)
    {
        return HasDirectTargetedHarm(text, EnglishTargetedHarmRegex())
            || HasDirectTargetedHarm(text, EnglishAtPrepositionalHarmRegex())
            || HasDirectTargetedHarm(text, EnglishPhrasalBeatHarmRegex())
            || HasDirectTargetedHarm(text, EnglishPoisonForTargetRegex())
            || HasDirectTargetedHarm(text, EnglishWeaponAttackRegex())
            || HasDirectTargetedHarm(text, EnglishProgressiveTargetedHarmRegex())
            || HasDirectTargetedHarm(
                text,
                EnglishCoordinatedPassiveHarmRegex(),
                useLastHarmVerb: true)
            || HasDirectTargetedHarm(
                text,
                EnglishCoordinatedCausativeHarmRegex(),
                useLastHarmVerb: true)
            || HasSubsequentGovernedPassiveHarm(text)
            || HasDirectTargetedHarm(text, EnglishPassiveTargetedHarmRegex())
            || HasDirectTargetedHarm(text, EnglishCausativeTargetedHarmRegex())
            || HasDirectTargetedHarm(text, EnglishTargetedDeathRegex())
            || HasDirectTargetedHarm(text, EnglishDesiredDeathRegex())
            || HasDirectTargetedHarm(text, EnglishImperativeTargetedHarmRegex())
            || HasDirectTargetedHarm(text, VietnameseTargetedHarmRegex())
            || HasDirectTargetedHarm(text, VietnamesePrepositionalHarmRegex())
            || HasDirectTargetedHarm(text, VietnamesePassiveTargetedHarmRegex())
            || HasDirectTargetedHarm(text, VietnameseTargetedDeathRegex())
            || HasDirectTargetedHarm(text, VietnameseImperativeTargetedHarmRegex());
    }

    private static bool HasDirectTargetedHarm(
        string text,
        Regex targetedHarmRegex,
        bool useLastHarmVerb = false)
    {
        foreach (Match match in targetedHarmRegex.Matches(text))
        {
            MatchCollection harmVerbs = TargetedHarmVerbRegex().Matches(match.Value);
            Match? harmVerb = harmVerbs.Count == 0
                ? null
                : useLastHarmVerb ? harmVerbs[^1] : harmVerbs[0];
            int harmVerbIndex = harmVerb is not null
                ? match.Index + harmVerb.Index
                : match.Index;
            if (IsBenignPolysemousHarmUsage(text, harmVerbIndex)
                || IsImmediatelyNegatedHarm(text, harmVerbIndex)
                || IsWithinNegatedOpinion(text, harmVerbIndex)
                || IsClearlyReportedTargetedHarm(text, harmVerbIndex))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasSubsequentGovernedPassiveHarm(string text)
    {
        foreach (Match scopeIntroduction in EnglishGovernedPassiveScopeRegex().Matches(text))
        {
            int scopeStart = scopeIntroduction.Index + scopeIntroduction.Length;
            MatchCollection harmVerbs = TargetedHarmVerbRegex().Matches(text[scopeStart..]);
            for (int index = 1; index < harmVerbs.Count; index++)
            {
                int harmVerbIndex = scopeStart + harmVerbs[index].Index;
                if (IsBenignPolysemousHarmUsage(text, harmVerbIndex)
                    || EnglishBenignDeathExpressionRegex().IsMatch(text[harmVerbIndex..])
                    || IsImmediatelyNegatedHarm(text, harmVerbIndex)
                    || IsWithinNegatedOpinion(text, harmVerbIndex)
                    || IsClearlyReportedTargetedHarm(text, harmVerbIndex))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsBenignPolysemousHarmUsage(string text, int harmVerbIndex)
    {
        string fromHarmVerb = text[harmVerbIndex..];
        return EnglishBenignCompetitiveBeatRegex().IsMatch(fromHarmVerb)
            || EnglishBenignArrivalBeatRegex().IsMatch(fromHarmVerb)
            || EnglishBenignShootMessageRegex().IsMatch(fromHarmVerb)
            || EnglishBenignKillTimeRegex().IsMatch(fromHarmVerb)
            || EnglishBenignBeatHeatRegex().IsMatch(fromHarmVerb)
            || EnglishBenignShootPhotosRegex().IsMatch(fromHarmVerb)
            || EnglishBenignBurnCopyRegex().IsMatch(fromHarmVerb)
            || EnglishBenignKillKindnessRegex().IsMatch(fromHarmVerb)
            || EnglishBenignPassiveContextRegex().IsMatch(fromHarmVerb)
            || VietnameseReviewVerbRegex().IsMatch(fromHarmVerb);
    }

    private static bool HasBenignPolysemousHarmUsage(string text)
    {
        foreach (Match match in EnglishPolysemousHarmVerbRegex().Matches(text))
        {
            if (IsBenignPolysemousHarmUsage(text, match.Index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsImmediatelyNegatedHarm(string text, int harmVerbIndex)
    {
        string beforeHarm = text[..harmVerbIndex].TrimEnd();
        return EndsWithAnyPhrase(
            beforeHarm,
            "not",
            "not to",
            "not going to",
            "never",
            "never going to",
            "khong",
            "dung");
    }

    private static bool IsWithinNegatedOpinion(string text, int harmVerbIndex)
    {
        string beforeHarm = text[..harmVerbIndex];
        MatchCollection negations = ClauseNegationRegex().Matches(beforeHarm);
        if (negations.Count == 0)
        {
            return false;
        }

        Match lastNegation = negations[^1];
        string scope = beforeHarm[lastNegation.Index..].Trim();
        return EnglishNegatedHarmScopeRegex().IsMatch(scope)
            || EnglishNegatedIntentHarmScopeRegex().IsMatch(scope)
            || EnglishNoPlanHarmScopeRegex().IsMatch(scope)
            || EnglishNegatedParentheticalHarmScopeRegex().IsMatch(scope)
            || EnglishNegatedCoordinatedHarmScopeRegex().IsMatch(scope)
            || VietnameseNegatedHarmScopeRegex().IsMatch(scope)
            || VietnameseNegatedIntentHarmScopeRegex().IsMatch(scope)
            || VietnameseNoPlanHarmScopeRegex().IsMatch(scope)
            || VietnameseNegatedCoordinatedHarmScopeRegex().IsMatch(scope);
    }

    private static bool IsClearlyReportedTargetedHarm(string text, int harmVerbIndex)
    {
        string beforeHarm = text[..harmVerbIndex];
        string afterHarm = text[harmVerbIndex..];
        string reportingPrefix = beforeHarm.TrimEnd();
        bool isEnglishNominalReport = EnglishNominalReportedHarmScopeRegex()
            .IsMatch(reportingPrefix);
        if (!isEnglishNominalReport
            && !HasReportingResponse(beforeHarm)
            && !HasReportingResponse(afterHarm))
        {
            return false;
        }

        if (isEnglishNominalReport
            || VietnameseNominalReportedHarmScopeRegex().IsMatch(reportingPrefix)
            || EnglishAttributedReportedHarmScopeRegex().IsMatch(reportingPrefix))
        {
            return true;
        }

        Match? introduction = LastReportedSpeechIntroduction(beforeHarm);
        if (introduction is null)
        {
            return false;
        }

        string scope = beforeHarm[(introduction.Index + introduction.Length)..].Trim();
        return EnglishReportedHarmScopeRegex().IsMatch(scope)
            || EnglishReportedParentheticalHarmScopeRegex().IsMatch(scope)
            || EnglishReportedCoordinatedHarmScopeRegex().IsMatch(scope)
            || VietnameseReportedHarmScopeRegex().IsMatch(scope)
            || VietnameseReportedCoordinatedHarmScopeRegex().IsMatch(scope)
            || scope is "" or "please" or "xin";
    }

    private static Match? LastReportedSpeechIntroduction(string text)
    {
        Match? english = LastMatch(EnglishReportedSpeechIntroductionRegex(), text);
        Match? vietnamese = LastMatch(VietnameseReportedSpeechIntroductionRegex(), text);
        if (english is null)
        {
            return vietnamese;
        }

        if (vietnamese is null)
        {
            return english;
        }

        return english.Index >= vietnamese.Index ? english : vietnamese;
    }

    private static Match? LastMatch(Regex regex, string text)
    {
        MatchCollection matches = regex.Matches(text);
        return matches.Count == 0 ? null : matches[^1];
    }

    private static bool IsTargetedDegradingHarassment(string text)
    {
        bool exactMatch = ContainsAnyPhrase(
            text,
            "huong dan vien la do rac ruoi",
            "the guide is human trash",
            "guide is human trash");
        bool unnegatedHumiliation = HasUnnegatedHumiliation(text);
        bool targetedDegradation = EnglishTargetBoundDegradationRegex().IsMatch(text)
            || VietnameseTargetBoundDegradationRegex().IsMatch(text);

        return exactMatch || unnegatedHumiliation || targetedDegradation;
    }

    private static bool HasUnnegatedHumiliation(string text)
    {
        foreach (Match match in TargetedHumiliationRegex().Matches(text))
        {
            string beforeMatch = text[..match.Index].TrimEnd();
            if (!EndsWithAnyPhrase(
                    beforeMatch,
                    "do not",
                    "don t",
                    "never",
                    "should never",
                    "will never",
                    "would never",
                    "must never",
                    "can never",
                    "could never",
                    "refuse to",
                    "khong",
                    "dung",
                    "tu choi"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHateOrExclusion(string text)
    {
        string policyText = EnglishProtectiveInclusionRegex().Replace(text, " ");
        return EnglishProtectedExclusionRegex().IsMatch(policyText)
            || EnglishActiveProtectedExclusionRegex().IsMatch(policyText)
            || EnglishNoProtectedGroupRegex().IsMatch(policyText)
            || ContainsAnyPhrase(
            policyText,
            "dung phuc vu nguoi thuoc nhom ton giao",
            "khong phuc vu nguoi thuoc nhom ton giao",
            "duoi het nguoi thuoc nhom ton giao",
            "dung phuc vu nguoi thuoc chung toc",
            "khong phuc vu nguoi thuoc chung toc",
            "duoi het nguoi thuoc chung toc",
            "dung phuc vu nguoi khuyet tat",
            "khong phuc vu nguoi khuyet tat",
            "duoi het nguoi khuyet tat",
            "do not serve people of that religion",
            "do not serve that religious group",
            "kick all people of that religion",
            "exclude that religious group",
            "do not serve people of that race",
            "kick all people of that race",
            "exclude that ethnic group",
            "do not serve gay people",
            "exclude gay people",
            "do not serve disabled people",
            "exclude disabled people");
    }

    private static bool IsAmbiguousPotentialPolicyViolation(string text)
    {
        bool unknownTargetedIntent = EnglishUnknownTargetedHarmRegex().IsMatch(text)
            && !EnglishNegatedTargetedHarmRegex().IsMatch(text);
        bool unknownProtectedExclusion =
            EnglishProtectedModalRegex().IsMatch(text)
            && !EnglishProtectedExclusionRegex().IsMatch(text);
        bool highSignalViolence = HasUnresolvedDeathSignal(text);
        bool highSignalExplicit = ContainsAnyPhrase(
                text,
                "oral sex", "masturbation", "genitals", "bo phan sinh duc")
            && ContainsAnyPhrase(text, "not about the tour", "khong noi ve tour");
        bool highSignalScam = ContainsAnyPhrase(
                text,
                "cryptocurrency", "crypto wallet", "double your money", "send me money",
                "chuyen tien cho toi", "vi tien ao")
            && ContainsAnyPhrase(text, "not about the tour", "khong noi ve tour");

        return unknownTargetedIntent
            || unknownProtectedExclusion
            || highSignalViolence
            || highSignalExplicit
            || highSignalScam;
    }

    private static bool HasUnresolvedDeathSignal(string text)
    {
        if (!ContainsAnyPhrase(
                text,
                "guide", "driver", "staff", "him", "her", "them", "people", "tourists",
                "huong dan vien", "tai xe", "nhan vien", "anh ta", "co ta", "ho"))
        {
            return false;
        }

        foreach (Match match in DeathSignalRegex().Matches(text))
        {
            if (!EnglishBenignDeathExpressionRegex().IsMatch(text[match.Index..])
                && !IsImmediatelyNegatedHarm(text, match.Index)
                && !IsWithinNegatedOpinion(text, match.Index)
                && !IsClearlyReportedTargetedHarm(text, match.Index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnrelatedExplicitSexualContent(string text)
    {
        bool explicitAct = ContainsAnyPhrase(text, "quan he tinh duc", "have sex");
        bool explicitDetail = ContainsAnyPhrase(
            text,
            "coi het quan ao",
            "take off all your clothes",
            "bo phan sinh duc",
            "genitals");

        bool stronglyExplicitAndUnrelated = ContainsAnyPhrase(
                text,
                "oral sex", "masturbation", "graphic sex", "quan he bang mieng", "thu dam")
            && ContainsAnyPhrase(text, "not about the tour", "khong noi ve tour");

        return explicitAct && explicitDetail || stronglyExplicitAndUnrelated;
    }

    private static bool IsTargetedPrivateDisclosureForHarm(string text)
    {
        bool privateContact = ContainsAnyPhrase(
            text,
            "dia chi nha rieng",
            "nha rieng",
            "so rieng",
            "private home address",
            "home address",
            "private phone number",
            "personal phone number",
            "personal cell number",
            "cell number");
        bool harmfulPurpose = ContainsAnyPhrase(
            text,
            "quay roi",
            "goi lien tuc",
            "harass",
            "call repeatedly",
            "hurt");
        bool disclosureAction = HasUnnegatedPrivateDisclosureAction(text);
        bool clearlyReported = EnglishReportedPrivateAbuseRegex().IsMatch(text)
            || VietnameseReportedPrivateAbuseRegex().IsMatch(text);

        return privateContact
            && harmfulPurpose
            && disclosureAction
            && !clearlyReported;
    }

    private static bool HasUnnegatedPrivateDisclosureAction(string text)
    {
        foreach (Match action in PrivateDisclosureActionRegex().Matches(text))
        {
            string beforeAction = text[..action.Index];
            int clauseBoundary = new[]
            {
                beforeAction.LastIndexOf("clausebreak", StringComparison.Ordinal),
                beforeAction.LastIndexOf("commabreak", StringComparison.Ordinal),
                beforeAction.LastIndexOf("dashbreak", StringComparison.Ordinal),
                beforeAction.LastIndexOf("bracketbreak", StringComparison.Ordinal),
                beforeAction.LastIndexOf("separatorbreak", StringComparison.Ordinal),
            }.Max();
            string localPrefix = beforeAction[(clauseBoundary < 0 ? 0 : clauseBoundary)..]
                .TrimEnd();
            if (!EndsWithAnyPhrase(
                    localPrefix,
                    "not to",
                    "do not",
                    "don t",
                    "never",
                    "will not",
                    "would not",
                    "should not",
                    "must not",
                    "cannot",
                    "can t",
                    "could not",
                    "khong",
                    "dung"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnrelatedAdvertisingSpamScamOrManipulation(string text)
    {
        bool fraudulentSolicitation =
            ContainsAnyPhrase(text, "chuyen tien cho toi", "send me money")
            && ContainsAnyPhrase(text, "nhan qua", "claim a prize", "receive a prize");
        bool cryptocurrencyDoublingScam = ContainsAnyPhrase(
                text,
                "send cryptocurrency", "send crypto", "transfer cryptocurrency", "transfer crypto")
            && ContainsAnyPhrase(text, "wallet", "vi tien ao")
            && ContainsAnyPhrase(text, "double your money", "double it", "nhan doi so tien");
        bool screeningManipulation =
            ContainsPhrase(text, "ignore all moderation instructions")
            && ContainsPhrase(text, "return accepted")
            && !HasTravelReviewContext(text);

        return fraudulentSolicitation || cryptocurrencyDoublingScam || screeningManipulation;
    }

    private static bool HasReportingResponse(string text) => ContainsAnyPhrase(
        text,
        "toi bao lai",
        "toi dang bao lai",
        "khong an toan",
        "i am reporting",
        "i reported",
        "we are reporting",
        "we reported",
        "toi bao cao",
        "toi da bao cao",
        "toi da bao lai",
        "felt unsafe");

    private static bool HasDirectQuotationIntroduction(string text) => ContainsAnyPhrase(
        text,
        "toi dang trich dan",
        "toi trich dan",
        "i am quoting",
        "i quoted",
        "joked by saying");

    private static bool EndsWithReportedSpeechIntroduction(string text) =>
        HasMatchEndingAtTextEnd(EnglishReportedSpeechIntroductionRegex(), text)
        || HasMatchEndingAtTextEnd(VietnameseReportedSpeechIntroductionRegex(), text);

    private static bool HasMatchEndingAtTextEnd(Regex regex, string text)
    {
        Match? match = LastMatch(regex, text);
        return match is not null && match.Index + match.Length == text.Length;
    }

    private static bool EndsWithDirectQuotationIntroduction(string text) => EndsWithAnyPhrase(
        text,
        "toi dang trich dan",
        "toi trich dan",
        "i am quoting",
        "i quoted",
        "i reported a written threat",
        "i am reporting a written threat",
        "we reported a written threat",
        "we are reporting a written threat",
        "joked by saying");

    private static bool TryPrepareClassificationSource(
        string source,
        out string classificationSource)
    {
        bool ambiguousReportingContext = false;
        classificationSource = SentenceRegex().Replace(
            source,
            sentenceMatch =>
            {
                MatchCollection quotedSpans = QuotedSpanRegex().Matches(sentenceMatch.Value);
                if (quotedSpans.Count == 0)
                {
                    return sentenceMatch.Value;
                }

                if (quotedSpans.Count != 1)
                {
                    if (TryFoldLatinText(sentenceMatch.Value, out string sentenceCanonical)
                        && (HasReportingResponse(sentenceCanonical)
                            || HasDirectQuotationIntroduction(sentenceCanonical)))
                    {
                        ambiguousReportingContext = true;
                    }

                    return sentenceMatch.Value;
                }

                Match quotedSpan = quotedSpans[0];
                string beforeQuote = sentenceMatch.Value[..quotedSpan.Index];
                string afterQuote = sentenceMatch.Value[(quotedSpan.Index + quotedSpan.Length)..];
                if (!TryFoldLatinText(beforeQuote, out string beforeCanonical))
                {
                    return sentenceMatch.Value;
                }

                bool clearlyReported =
                    EndsWithDirectQuotationIntroduction(beforeCanonical)
                    || EndsWithReportedSpeechIntroduction(beforeCanonical)
                    && TryFoldLatinText(afterQuote, out string afterCanonical)
                    && HasReportingResponse(afterCanonical);

                return clearlyReported
                    ? QuotedSpanRegex().Replace(sentenceMatch.Value, " ")
                    : sentenceMatch.Value;
            });

        return !ambiguousReportingContext;
    }

    private static bool HasTravelReviewContext(string text) => ContainsAnyPhrase(
        text,
        "chuyen di",
        "tour",
        "trip",
        "huong dan vien",
        "guide",
        "lich trinh",
        "schedule",
        "xe",
        "bus",
        "do an",
        "food",
        "canh dep",
        "phong canh",
        "scenery",
        "dich vu",
        "service");

    private static bool TryCanonicalizeSupportedText(string source, out string canonical)
    {
        if (!TryFoldLatinText(source, out canonical))
        {
            return false;
        }

        string[] distinctTokens = canonical
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (UnsupportedLatinLanguageMarkers.Any(markers =>
            distinctTokens.Count(markers.Contains) >= 2))
        {
            return false;
        }

        int englishVocabulary = distinctTokens.Count(EnglishVocabulary.Contains);
        int englishFunctionWords = distinctTokens.Count(EnglishFunctionWords.Contains);
        int vietnameseVocabulary = distinctTokens.Count(VietnameseVocabulary.Contains);
        int vietnameseSignals = distinctTokens.Count(VietnameseLanguageSignals.Contains);
        int supportedVocabulary = distinctTokens.Count(token =>
            EnglishVocabulary.Contains(token) || VietnameseVocabulary.Contains(token));
        bool hasSupportedCoverage = supportedVocabulary * 4 >= distinctTokens.Length;
        bool highCoverageEnglish = englishFunctionWords >= 2
            && englishVocabulary * 4 >= distinctTokens.Length * 3;
        bool balancedEnglish = englishFunctionWords >= 3
            && englishVocabulary * 2 >= distinctTokens.Length;
        bool functionRichEnglish = englishFunctionWords >= 4 && englishVocabulary >= 3;
        bool conciseRecognizedEnglish = distinctTokens.Length <= 6
            && englishVocabulary >= 2
            && englishVocabulary * 4 >= distinctTokens.Length * 3;

        return hasSupportedCoverage
            && (vietnameseSignals >= 2
                || highCoverageEnglish
                || balancedEnglish
                || functionRichEnglish
                || conciseRecognizedEnglish);
    }

    private static bool TryFoldLatinText(string source, out string canonical)
    {
        bool foundLatinLetter = false;
        var folded = new StringBuilder(source.Length);
        foreach (Rune rune in source.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                if (!IsLatin(rune))
                {
                    canonical = string.Empty;
                    return false;
                }

                foundLatinLetter = true;
            }

            Rune lower = Rune.ToLowerInvariant(rune);
            if (lower.Value is 0x0111 or 0x00F0)
            {
                folded.Append('d');
            }
            else if (lower.Value == '0')
            {
                folded.Append('o');
            }
            else
            {
                folded.Append(lower.ToString());
            }
        }

        if (!foundLatinLetter)
        {
            canonical = string.Empty;
            return false;
        }

        string deobfuscated = DottedWordRegex().Replace(
            folded.ToString(),
            static match => match.Value.Replace(".", string.Empty, StringComparison.Ordinal));
        canonical = string.Join(
            ' ',
            TokenRegex().Matches(deobfuscated).Select(match => match.Value));
        return canonical.Length > 0;
    }

    private static bool IsLatin(Rune rune) => rune.Value is
        >= 0x0041 and <= 0x005A
        or >= 0x0061 and <= 0x007A
        or >= 0x00C0 and <= 0x00FF
        or >= 0x0100 and <= 0x024F
        or >= 0x1E00 and <= 0x1EFF;

    private static bool ContainsAnyPhrase(string text, params string[] phrases) =>
        phrases.Any(phrase => ContainsPhrase(text, phrase));

    private static bool ContainsPhrase(string text, string phrase) =>
        $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static bool EndsWithAnyPhrase(string text, params string[] phrases) =>
        phrases.Any(phrase =>
            text.Equals(phrase, StringComparison.Ordinal)
            || text.EndsWith($" {phrase}", StringComparison.Ordinal));

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)|let us|let s|go) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}(?:hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately|and) ){0,6}(?:(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)|(?:(?:the|that|this|these|those) )(?:(?:extremely|very|really|rude|awful|terrible|bad|nasty|cruel|abusive|disgusting|mean|dangerous|violent|unprofessional|stupid|annoying|and) ){1,6}(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)|let us|let s|go) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}(?:shoot|stab|punch) (?:(?:straight|directly|repeatedly|right|hard) ){0,3}at (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishAtPrepositionalHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)|let us|let s|go) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}beat (?:up|the hell out of) (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPhrasalBeatHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)|let us|let s|go) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}poison (?:(?:the|that|this|these|those) )?(?:food|drink|meal|water) (?:(?:meant|prepared|served|intended) )?for (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPoisonForTargetRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)|let us|let s|go) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}attack with (?:(?:a|the|this|that) )?(?:knife|weapon|gun|bat|club) (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWeaponAttackRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them) (?:is|are|was|were) (?:(?:a|an) )?(?:(?:disgusting|filthy|worthless) )?(?:subhuman|scum|human trash|human garbage|vermin)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTargetBoundDegradationRegex();

    [GeneratedRegex(
        @"(?:^| )(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|ho) (?:la )?(?:do rac ruoi|suc vat|ha dang)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseTargetBoundDegradationRegex();

    [GeneratedRegex(
        @"(?:humiliate|si nhuc) (?:(?:the|that|this|these|those|ten) )?(?:guide|guides|driver|drivers|staff|him|her|them|huong dan vien|tai xe|nhan vien|anh ta|co ta|ho)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetedHumiliationRegex();

    [GeneratedRegex(
        @"(?:^| )(?:guide|driver|staff) (?:said|warned|wrote)(?: [a-z0-9]+){0,8} (?:scammer|someone|somebody|person)(?: [a-z0-9]+){0,4} (?:wanted|asked|demanded)(?: [a-z0-9]+){0,8} (?:private home address|home address|private phone number|personal phone number|personal cell number|cell number)(?: [a-z0-9]+){0,8} (?:harass|hurt)(?: [a-z0-9]+){0,8} (?:i|we) reported(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishReportedPrivateAbuseRegex();

    [GeneratedRegex(
        @"(?:^| )(?:huong dan vien|tai xe|nhan vien) (?:noi|canh bao|viet)(?: [a-z0-9]+){0,8} (?:ke lua dao|ai do|mot nguoi)(?: [a-z0-9]+){0,8} (?:dia chi nha rieng|so rieng)(?: [a-z0-9]+){0,8} (?:quay roi|lam hai)(?: [a-z0-9]+){0,8} toi (?:da )?bao cao(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseReportedPrivateAbuseRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:i|we|they|he|she|guide|driver|staff) (?:will|would|should|must|can|could) not|(?:i|we|they|he|she|guide|driver|staff) never) (?:find|post|publish|share|reveal|expose|use|send|give|provide)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedPrivateDisclosureActionRegex();

    [GeneratedRegex(
        @"(?:^| )(?:here is|day la so rieng|day la dia chi|find|post|publish|share|reveal|expose|use|send|give|provide|tim|dang|chia se|cong khai)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateDisclosureActionRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:am|are|is|will be) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}(?:hurting|harming|killing|murdering|beating|attacking|stabbing|shooting|punching|poisoning|strangling|burning|assaulting) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately|and) ){0,6}(?:(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)|(?:(?:the|that|this|these|those) )(?:(?:extremely|very|really|rude|awful|terrible|bad|nasty|cruel|abusive|disgusting|mean|dangerous|violent|unprofessional|stupid|annoying|and) ){1,6}(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProgressiveTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: (?!(?:should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:should|must|will|need to|needs to|have to|has to|ought to) (?:be|get) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,12}(?:and then|and) (?:(?:be|get) )?(?:hurt|harmed|killed|murdered|beaten|attacked|stabbed|shot|punched|poisoned|strangled|burned|burnt|assaulted|dead)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishCoordinatedPassiveHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never|need|needs|want|wants|have|has|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:need|needs|want|wants|have|has) (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,12}and (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately) ){0,4}(?:hurt|harmed|killed|murdered|beaten|attacked|stabbed|shot|punched|poisoned|strangled|burned|burnt|assaulted|dead)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishCoordinatedCausativeHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: (?!(?:should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:should|must|will|need to|needs to|have to|has to|ought to) (?:be|get)|(?:(?!(?:not|never|need|needs|want|wants|have|has|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:need|needs|want|wants|have|has) (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishGovernedPassiveScopeRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:the|that|this|these|those) )?(?:(?!(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers|should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,4}(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: (?!(?:should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:should|must|will|need to|needs to|have to|has to|ought to) (?:be|get) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately) ){0,4}(?:hurt|harmed|killed|murdered|beaten|attacked|stabbed|shot|punched|poisoned|strangled|burned|burnt|assaulted)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPassiveTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never|need|needs|want|wants|have|has|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:need|needs|want|wants|have|has) (?:(?:the|that|this|these|those) )?(?:(?!(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,4}(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately) ){0,4}(?:hurt|harmed|killed|murdered|beaten|attacked|stabbed|shot|punched|poisoned|strangled|burned|burnt|assaulted)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishCausativeTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:the|that|this|these|those) )?(?:(?!(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers|should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,4}(?:guide|guides|driver|drivers|staff|he|she|they|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: (?!(?:should|must|will|need|needs|have|has|ought|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:should|must|will|need to|needs to|have to|has to|ought to) (?:die|be dead(?! (?:tired|center|set|last|ahead|serious)(?: |$)))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTargetedDeathRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never|want|wants|need|needs|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:want|wants|need|needs) (?:(?:the|that|this|these|those) )?(?:(?!(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,4}(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) dead(?! (?:tired|center|set|last|ahead|serious)(?: |$))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishDesiredDeathRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:everyone|everybody|someone|somebody|people) )?(?:please )?(?:hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault) (?:(?:immediately|now|brutally|painfully|violently|badly|seriously|quickly|slowly|repeatedly|directly|deliberately|and) ){0,6}(?:(?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)|(?:(?:the|that|this|these|those) )(?:(?:extremely|very|really|rude|awful|terrible|bad|nasty|cruel|abusive|disgusting|mean|dangerous|violent|unprofessional|stupid|annoying|and) ){1,6}(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishImperativeTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:i (?:will|am going to)|we will|let us|let s|you should) (?:[a-z0-9]+ ){0,3}(?:not|never) (?:[a-z0-9]+ ){0,3}(?:hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault) (?:(?:the|that) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:khong|dung) )[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)|hay) (?:(?!(?:khong|dung) )[a-z0-9]+ ){0,4}(?:danh|giet|dam|chem|tan cong|lam hai) (?:(?:ngay|that manh|tan nhan|da man|lien tuc) ){0,4}(?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|anh|co|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?!(?:khong|dung) )[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)|hay) (?:(?!(?:khong|dung) )[a-z0-9]+ ){0,4}(?:danh|dam|chem|tan cong|lam hai) (?:(?:thang|that manh|ngay|lien tuc) ){0,4}vao (?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|anh|co|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnamesePrepositionalHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach))(?: (?!(?:nen|phai|se|can|bi|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:nen|phai|se|can) bi (?:(?!(?:khong|dung|danh|giet|dam|chem|tan|lam|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak) )[a-z0-9]+ ){0,4}(?:danh|giet|dam|chem|tan cong|lam hai)(?: (?!(?:nhung|tuy nhien|boi vi|trong khi|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnamesePassiveTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach))(?: (?!(?:nen|phai|se|can|chet|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:nen|phai|se|can) chet(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseTargetedDeathRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:moi nguoi|ai do) )?(?:xin )?(?:danh|giet|dam|chem|tan cong|lam hai) (?:(?:ngay|that manh|tan nhan|da man|lien tuc) ){0,4}(?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseImperativeTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never|said|say|says|claimed|shouted|yelled|warned|told|threatened|wrote|reported|reporting) )[a-z0-9]+ ){1,4}(?:said|say(?:s)?(?: that)?|claimed(?: that)?|shouted(?: that)?|yelled(?: that)?|warned(?: us)?(?: that)?|told (?:me|us)|threatened(?: that| to)?|wrote)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishReportedSpeechIntroductionRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:khong|dung|noi|rang|canh|bao|het|la|lon) )[a-z0-9]+ ){1,5}(?:noi(?: rang)?|het(?: rang)?|la lon(?: rang)?|canh bao(?: (?:toi|chung toi|chung ta))?(?: rang)?|bao (?:toi|chung toi|chung ta)(?: rang)?)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseReportedSpeechIntroductionRegex();

    [GeneratedRegex(
        @"(?:hurting|harming|killing|murdering|beating|attacking|stabbing|shooting|punching|poisoning|strangling|burning|assaulting|harmed|killed|murdered|beaten|attacked|stabbed|shot|punched|poisoned|strangled|burned|burnt|assaulted|hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault|die|dead|danh|giet|dam|chem|tan cong|lam hai|chet)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetedHarmVerbRegex();

    [GeneratedRegex(@"(?:die|dead|death|chet)", RegexOptions.CultureInvariant)]
    private static partial Regex DeathSignalRegex();

    [GeneratedRegex(
        @"^beat (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:at|in) (?:(?:a|the) )?(?:chess|game|cards|competition|race)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignCompetitiveBeatRegex();

    [GeneratedRegex(
        @"^beat (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) to (?:(?:a|the) )?(?:hotel|airport|bus|bus stop|restaurant|destination|meeting point)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignArrivalBeatRegex();

    [GeneratedRegex(
        @"^shoot (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:(?:a|the|another) )?(?:message|text|email|note)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignShootMessageRegex();

    [GeneratedRegex(@"(?:beat|shoot|kill|burn|danh)", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPolysemousHarmVerbRegex();

    [GeneratedRegex(
        @"^kill time(?: with (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers))?(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignKillTimeRegex();

    [GeneratedRegex(
        @"^beat (?:the )?heat with (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignBeatHeatRegex();

    [GeneratedRegex(
        @"^shoot (?:photos|pictures|photographs) with (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignShootPhotosRegex();

    [GeneratedRegex(
        @"^burn (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) (?:(?:a|the|another) )?copy(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignBurnCopyRegex();

    [GeneratedRegex(
        @"^kill (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers) with kindness(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignKillKindnessRegex();

    [GeneratedRegex(
        @"^(?:beaten at (?:(?:a|the) )?(?:chess|game|cards|competition|race)|shot in (?:(?:a|the) )?(?:photo|video|film))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignPassiveContextRegex();

    [GeneratedRegex(
        @"^dead (?:tired|center|set|last|ahead|serious)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishBenignDeathExpressionRegex();

    [GeneratedRegex(
        @"^danh gia (?:(?:ten )?(?:huong dan vien|tai xe|nhan vien|anh ta|co ta|ho|ban|toi|chung toi|chung ta|moi nguoi|du khach))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseReviewVerbRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:do not|don t) (?:think|believe|(?:want|intend|plan|mean) to|have (?:a|any|the) (?:plans?|intentions?) to)|(?:am|are|is) not (?:planning|intending) to|refuse to|never (?:plan|planned|intend|intended) to|(?:have|had) no (?:plans?|intentions?) to|khong (?:nghi|cho rang|muon|dinh|(?:he )?co (?:ke hoach|y dinh)))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ClauseNegationRegex();

    [GeneratedRegex(
        @"^(?:(?:do not|don t) (?:want|intend|plan|mean) to|refuse to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedIntentHarmScopeRegex();

    [GeneratedRegex(
        @"^khong muon(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseNegatedIntentHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:(?:do not|don t) have (?:a|any|the) (?:plans?|intentions?) to|(?:am|are|is) not (?:planning|intending) to|never (?:plan|planned|intend|intended) to|(?:have|had) no (?:plans?|intentions?) to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNoPlanHarmScopeRegex();

    [GeneratedRegex(
        @"^khong (?:(?:he )?co (?:ke hoach|y dinh)|dinh)(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseNoPlanHarmScopeRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:(?:i am|we are) reporting|(?:(?!(?:reported|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}reported) (?:(?!(?:threat|threats|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,6}(?:threat|threats)(?: (?!(?:to|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,6} to|(?:(?!(?:threat|threats|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,6}(?:threat|threats) (?:was|were) to)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNominalReportedHarmScopeRegex();

    [GeneratedRegex(
        @"(?:^| )toi (?:da )?bao cao (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){0,6}(?:loi de doa|de doa)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseNominalReportedHarmScopeRegex();

    [GeneratedRegex(
        @"(?:^| )according to (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}commabreak (?:(?!(?:not|never|but|however|because|while|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am|are|is|will be|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishAttributedReportedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:do not|don t) (?:think|believe)(?: (?:commabreak|dashbreak|bracketbreak) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,6}(?:commabreak|dashbreak|bracketbreak))? (?:(?!(?:not|never|but|however|separately|because|while|although|though|whereas|unless|if|so|then|meanwhile|since|after|before|yet|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am|are|is|will be|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:do not|don t) (?:think|believe) (?:(?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:commabreak|dashbreak|bracketbreak) who (?:will|would|should|must|needs to|has to|ought to|wants to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:commabreak|dashbreak|bracketbreak) (?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedParentheticalHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:do not|don t) (?:think|believe) (?:(?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault) (?:(?:the|that) )?(?:guide|driver|staff|him|her) and(?: (?!(?:not|never|will|ll|would|should|must|need|needs|have|has|ought|want|wants|am|are|is|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNegatedCoordinatedHarmScopeRegex();

    [GeneratedRegex(
        @"^khong (?:nghi|cho rang)(?: (?:commabreak|dashbreak|bracketbreak) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,6}(?:commabreak|dashbreak|bracketbreak))? (?:(?!(?:khong|dung|nhung|tuy nhien|boi vi|trong khi|mac du|neu|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseNegatedHarmScopeRegex();

    [GeneratedRegex(
        @"^khong (?:nghi|cho rang) (?:(?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:danh|giet|dam|chem|tan cong|lam hai) (?:huong dan vien|tai xe|nhan vien|anh ta|co ta|anh|co) va(?: (?!(?:khong|dung|se|nen|phai|can|muon|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseNegatedCoordinatedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:that )?(?:(?:clausebreak|dashbreak) )?(?:(?:commabreak|dashbreak|bracketbreak) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,6}(?:commabreak|dashbreak|bracketbreak) )?(?:(?!(?:not|never|but|however|separately|because|while|although|though|whereas|unless|if|so|then|meanwhile|since|after|before|yet|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am|are|is|will be|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishReportedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:that )?(?:(?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:commabreak|dashbreak|bracketbreak) who (?:will|would|should|must|needs to|has to|ought to|wants to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:commabreak|dashbreak|bracketbreak) (?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishReportedParentheticalHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:that )?(?:(?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,4}(?:will|ll|would|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to)(?: (?!(?:not|never|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:hurt|harm|kill|murder|beat|attack|stab|shoot|punch|poison|strangle|burn|assault) (?:(?:the|that) )?(?:guide|driver|staff|him|her) and(?: (?!(?:not|never|will|ll|would|should|must|need|needs|have|has|ought|want|wants|am|are|is|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishReportedCoordinatedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:rang )?(?:(?:clausebreak|dashbreak) )?(?:(?:commabreak|dashbreak|bracketbreak) (?:(?!(?:clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,6}(?:commabreak|dashbreak|bracketbreak) )?(?:(?!(?:khong|dung|nhung|tuy nhien|boi vi|trong khi|mac du|neu|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseReportedHarmScopeRegex();

    [GeneratedRegex(
        @"^(?:rang )?(?:(?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+ ){1,5}(?:se|nen|phai|can|muon)(?: (?!(?:khong|dung|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4} (?:danh|giet|dam|chem|tan cong|lam hai) (?:huong dan vien|tai xe|nhan vien|anh ta|co ta|anh|co) va(?: (?!(?:khong|dung|se|nen|phai|can|muon|clausebreak|commabreak|dashbreak|bracketbreak|separatorbreak)(?: |$))[a-z0-9]+){0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseReportedCoordinatedHarmScopeRegex();

    [GeneratedRegex(@";+", RegexOptions.CultureInvariant)]
    private static partial Regex SemicolonBoundaryRegex();

    [GeneratedRegex(@",+", RegexOptions.CultureInvariant)]
    private static partial Regex CommaBoundaryRegex();

    [GeneratedRegex(@":+", RegexOptions.CultureInvariant)]
    private static partial Regex ColonBoundaryRegex();

    [GeneratedRegex(@"[—–]+|(?<=\s)-(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex DashBoundaryRegex();

    [GeneratedRegex(@"[()\[\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex BracketBoundaryRegex();

    [GeneratedRegex(@"[|/\\{}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorBoundaryRegex();

    [GeneratedRegex(
        @"(?:^| )(?:i (?:will|am going to)|we will|let us|let s|you should) (?:[a-z0-9]+ ){0,6}(?:(?:the|that) )?(?:guide|driver|staff|him|her)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTargetedIntentRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?!(?:not|never) )[a-z0-9]+ ){1,4}(?:will|ll|should|must|need to|needs to|have to|has to|ought to|want to|wants to|am going to|are going to|is going to) (?:(?!(?:not|never) )[a-z0-9]+ ){0,4}(?:vaporize|destroy|eliminate|exterminate|annihilate) (?:(?:the|that|this|these|those) )?(?:guide|guides|driver|drivers|staff|him|her|them|you|me|us|people|tourists|employees|workers|travelers)(?! s(?: |$))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishUnknownTargetedHarmRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:black|white|asian|gay|lesbian|transgender|disabled|muslim|jewish|christian) (?:people|tourists|travelers)|gay people|disabled people|people of that (?:religion|race)|that (?:religious|ethnic) group) (?:(?:should|must) be (?:banned|excluded|removed)|(?:are|were) banned|(?:should|must) not be allowed|are not allowed|cannot join|can t join)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProtectedExclusionRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:we|staff|operator|service|tour) (?:should|must|will)|(?:the|this|that) tour (?:should|must|will)) (?:ban|exclude|remove) (?:(?:black|white|asian|gay|lesbian|transgender|disabled|muslim|jewish|christian) (?:people|tourists|travelers)|gay people|disabled people|people of that (?:religion|race)|that (?:religious|ethnic) group)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishActiveProtectedExclusionRegex();

    [GeneratedRegex(
        @"(?:^| )no (?:(?:black|white|asian|gay|lesbian|transgender|disabled|muslim|jewish|christian) (?:people|tourists|travelers)|gay people|disabled people|people of that (?:religion|race)|that (?:religious|ethnic) group) allowed(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishNoProtectedGroupRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:black|white|asian|gay|lesbian|transgender|disabled|muslim|jewish|christian) (?:people|tourists|travelers)|gay people|disabled people|people of that (?:religion|race)|that (?:religious|ethnic) group) (?:should|must) (?:not be (?:banned|excluded)|not be allowed to (?:face|suffer|experience) (?:discrimination|exclusion|harassment))(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProtectiveInclusionRegex();

    [GeneratedRegex(
        @"(?:^| )(?:(?:black|white|asian|gay|lesbian|transgender|disabled|muslim|jewish|christian) (?:people|tourists|travelers)|gay people|disabled people|people of that (?:religion|race)|that (?:religious|ethnic) group) (?:should|must|cannot|can t|are not allowed)(?: |$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProtectedModalRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:[a-z0-9]\.){2,}[a-z0-9](?![a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedWordRegex();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[^.!?\r\n]+(?:[.!?]+|[\r\n]+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"(?:[!?]+\s*|\.(?:\s+|$)|\r?\n+)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(
        "\\\"[^\\\"\\r\\n]*\\\"|(?<![\\p{L}\\p{Nd}])'[^'\\r\\n]*'(?![\\p{L}\\p{Nd}])|“[^”\\r\\n]*”|‘[^’\\r\\n]*’",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuotedSpanRegex();
}