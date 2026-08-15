using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// A node of the split tree. Splitting a pane only ever rewrites that pane's own subtree, so
    /// other panes keep their size and orientation.
    /// </summary>
    internal abstract class PaneNode
    {
        public PaneSplitNode? Parent { get; set; }

        /// <summary>The element that occupies this node's slot in its parent.</summary>
        public abstract FrameworkElement Element { get; }
    }

    /// <summary>A single pane: one <see cref="PaneGroup"/> with its own tab strip.</summary>
    internal sealed class PaneLeafNode : PaneNode
    {
        public PaneLeafNode(PaneGroup group) => Group = group;

        public PaneGroup Group { get; }

        public override FrameworkElement Element => Group;
    }

    /// <summary>
    /// Several children laid out in one direction, with dividers between them. N-ary rather than
    /// binary so that splitting again in the same direction produces equal panes instead of
    /// halving one side repeatedly.
    /// </summary>
    internal sealed class PaneSplitNode : PaneNode
    {
        public PaneSplitNode(Orientation orientation) => Orientation = orientation;

        public Orientation Orientation { get; }

        public Grid Panel { get; } = new();

        public List<PaneNode> Children { get; } = new();

        public override FrameworkElement Element => Panel;
    }
}
