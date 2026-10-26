namespace AetherVfx;

internal static class PreviewViewSlot
{
    public static ushort Validate(uint requestedIndex, uint actualIndex, uint objectId, int objectCapacity)
    {
        if (requestedIndex >= 8 || actualIndex != requestedIndex || objectCapacity <= 0 || objectId >= objectCapacity || objectId > ushort.MaxValue)
            throw new InvalidOperationException($"无效预览槽：requested={requestedIndex}, index={actualIndex}, id={objectId}, capacity={objectCapacity}");
        return (ushort)objectId;
    }
}
