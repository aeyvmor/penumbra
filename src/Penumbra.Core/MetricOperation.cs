namespace Penumbra.Core;

/// <summary>Fixed, low-cardinality operations that Penumbra may observe locally.</summary>
public enum MetricOperation
{
    RecognitionQuietPeriod = 0,
    RecognitionProcessing = 1,
    RecognitionPartition = 2,
    RecognitionClassification = 3,
    RecognitionGrammar = 4,
    SheetRecompute = 5,
    TaffyProcessing = 6,
    TaffyProbe = 7,
    TaffyGhostSynthesis = 8,
    TaffyPublication = 9,
    ExplicitSave = 10,
    Autosave = 11,
    RecoveryRead = 12,
    CloseFlush = 13,
    GraphDetection = 14,
    GraphSampling = 15,
}
