namespace TripMate.Domain.Enums;

public enum OperatorContractStatus { PendingSignature = 1, Signed = 2, Voided = 3 }
public enum OperatorContractDocumentState { NeedsSignerDetails = 1, Processing = 2, Ready = 3, Failed = 4 }
public enum OperatorContractChallengeStatus { Sending = 1, Sent = 2, Failed = 3, Used = 4 }