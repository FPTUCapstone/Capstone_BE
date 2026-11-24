namespace TripMate.Domain.Enums;

public enum ContractTemplateStatus { Draft = 1, Published = 2, Archived = 3 }

public enum ContractTemplateRenderState { NotRequested = 1, Processing = 2, Ready = 3, Failed = 4 }

public enum ContractTemplateRenderJobStatus { Queued = 1, Processing = 2, Completed = 3, Failed = 4 }