namespace ElizaAccess.UI.Elements
{
    /// <summary>
    /// A horizontal-row group — used for settings rows where the
    /// label sits to the left and the option buttons run across to
    /// the right (Display Mode: [Fullscreen] [Windowed], Auto-
    /// Advance: [Off] [Normal] [Slower], Language: [English] [中文]
    /// …). Functionally the same as the abstract <see cref="Container"/>
    /// base; the concrete subclass is a marker so future grid /
    /// column / page containers can carry layout-specific
    /// behaviour without affecting row groups.
    /// </summary>
    internal sealed class RowContainer : Container
    {
        public RowContainer(string? label) : base(label) { }
    }
}
