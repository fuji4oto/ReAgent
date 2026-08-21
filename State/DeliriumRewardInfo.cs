namespace ReAgent.State;

[Api]
public record DeliriumRewardInfo(
    [property: Api] string Id,
    [property: Api] string Name,
    [property: Api] int Count,
    [property: Api] float ProgressFraction);
