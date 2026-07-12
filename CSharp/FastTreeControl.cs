using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace LvControls
{
    /// <summary>Built-in indices in <see cref="LvTreeGrid.GlyphImages"/>.</summary>
    public enum LvTreeGlyph
    {
        None = 0,
        Unchecked = 1,
        Checked = 2,
        Startup = 3,
        Shutdown = 4
    }

    /// <summary>Details of a tree-cell click.</summary>
    public sealed class LvTreeCellClickedEventArgs : EventArgs
    {
        public string Tag { get; }
        public int ColumnIndex { get; }
        /// <summary>True when the click was on the expander or optional item glyph.</summary>
        public bool IsGlyphOrExpander { get; }

        public LvTreeCellClickedEventArgs(string tag, int columnIndex, bool isGlyphOrExpander)
        {
            Tag = tag;
            ColumnIndex = columnIndex;
            IsGlyphOrExpander = isGlyphOrExpander;
        }
    }

    /// <summary>
    /// One tree item. Roughly equivalent to a row in LabVIEW's Tree "Items[]" array,
    /// but also holds per-cell colour so painting is O(1) per visible cell.
    /// </summary>
    public class LvTreeItem
    {
        public string Tag;                 // unique key - same role as LabVIEW's item Tag
        public string ParentTag;           // null/"" = root
        public int Level;                  // indent depth, computed on insert/move
        public bool IsOpen = true;          // expand/collapse state ("Item.Set Open")
        public object[] CellValues;        // one entry per column, column 0 = label
        public Color[] CellBackColors;     // per-cell background, parallel to CellValues
        public Color[] CellForeColors;     // per-cell text colour
        public Color RowBackColor = Color.Empty; // used when a cell colour isn't set
        public bool Selected;
        /// <summary>True when this is the tree's active item, even if it is off-screen.</summary>
        public bool Active;
        /// <summary>Index in <see cref="LvTreeGrid.GlyphImages"/>; zero means no glyph.</summary>
        public int GlyphIndex = 0;

        public LvTreeItem Parent;
        public readonly List<LvTreeItem> Children = new List<LvTreeItem>();

        public bool HasChildren => Children.Count > 0;

        internal LvTreeItem(string tag, string parentTag, int columnCount)
        {
            Tag = tag;
            ParentTag = parentTag;
            CellValues = new object[columnCount];
            CellBackColors = new Color[columnCount];
            CellForeColors = new Color[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                CellBackColors[i] = Color.Empty;
                CellForeColors[i] = Color.Empty;
            }
        }
    }

    /// <summary>
    /// Drop-in-ish replacement for the native LabVIEW Tree control.
    /// Method/property names deliberately mirror the LabVIEW invoke nodes/properties
    /// (Add Item, Remove Item, Item.Set Open, Items[], Active Item, Selected Items[],
    /// Top Item, Point To Row, Scroll To Item, etc.) so call sites port with minimal
    /// rewrites.
    ///
    /// Performance strategy:
    ///  - DataGridView in VirtualMode: grid only asks for data it's about to paint.
    ///  - Hierarchy is stored as a real tree, but a flattened "visible rows" list is
    ///    rebuilt only when structure/expand-state changes (not per paint).
    ///  - Per-cell colour lives on the node itself, not in a separate 10k x N array,
    ///    so SetCellColor is O(1) and painting is O(visible cells).
    ///  - Double buffering forced on via reflection (WinForms hides this property).
    ///  - Bulk-load path (BeginUpdate/EndUpdate) suspends layout and defers the
    ///    visible-rows rebuild + repaint until the whole batch is in.
    /// </summary>
    public class LvTreeGrid : UserControl
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly Dictionary<string, LvTreeItem> allItems = new Dictionary<string, LvTreeItem>();
        private readonly List<LvTreeItem> roots = new List<LvTreeItem>();
        private List<LvTreeItem> visibleRows = new List<LvTreeItem>();

        private int updateDepth = 0;
        private bool visibleRowsDirty = false;
        private bool allowUserSelection = true;
        private bool ensureActiveItemVisible = true;
        private bool suppressingUserSelection = false;
        // Set to true while ActiveItem/ActiveItemTag is being set programmatically so
        // the SelectionChanged handler never clears a programmatic highlight.
        private bool isProgrammaticSelection = false;
        // The active item to retain while user selection is disabled. A click can
        // change DataGridView.CurrentCell even after its selection is cleared.
        private string lockedActiveItemTag;
        private string activeItemTag;
        private ImageList glyphImages = CreateDefaultGlyphImages();

        private const int IndentPx = 16;
        private const int GlyphSize = 9;

        public event EventHandler<string> ActiveItemChanged;
        public event EventHandler<string> ItemDoubleClicked;
        public event EventHandler<string> ItemOpenedClosed;
        /// <summary>Raised when a data cell is clicked; inspect IsGlyphOrExpander for first-column glyph clicks.</summary>
        public event EventHandler<LvTreeCellClickedEventArgs> CellClicked;

        /// <summary>
        /// Images available for the optional glyph displayed to the left of each
        /// item's first-column text.
        /// </summary>
        public ImageList GlyphImages
        {
            get => glyphImages;
            set
            {
                glyphImages = value;
                grid.Invalidate();
            }
        }

        /// <summary>
        /// Creates the built-in unchecked, checked, startup, and shutdown glyphs.
        /// Use this to restore the defaults after assigning a custom image list.
        /// </summary>
        public static ImageList CreateDefaultGlyphImages()
        {
            var images = new ImageList
            {
                ColorDepth = ColorDepth.Depth32Bit,
                ImageSize = new Size(16, 16),
                TransparentColor = Color.Transparent
            };
            images.Images.Add(new Bitmap(16, 16));
            images.Images.Add(DrawCheckBoxGlyph(false));
            images.Images.Add(DrawCheckBoxGlyph(true));
            images.Images.Add(DrawPowerGlyph(Color.FromArgb(40, 145, 65)));
            images.Images.Add(DrawPowerGlyph(Color.FromArgb(190, 55, 45)));
            return images;
        }

        /// <summary>
        /// When false, the user cannot select rows by clicking or keyboard navigation.
        /// Programmatic selection via ActiveItem / ActiveItemTag still works.
        /// </summary>
        public bool AllowUserSelection
        {
            get => allowUserSelection;
            set
            {
                allowUserSelection = value;
                if (!allowUserSelection)
                {
                    lockedActiveItemTag = ActiveItem?.Tag;
                }
            }
        }

        public LvTreeGrid()
        {
            InitializeGrid();
        }

        private void InitializeGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            grid.ScrollBars = ScrollBars.Both;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            // WinForms deliberately hides DoubleBuffered on controls that don't
            // expose it publicly. DataGridView has heavy default flicker on
            // scroll/repaint at this row count without this.
            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, grid, new object[] { true });

            grid.CellValueNeeded += Grid_CellValueNeeded;
            grid.CellPainting += Grid_CellPainting;
            grid.CellMouseDown += Grid_CellMouseDown;
            grid.CellMouseClick += Grid_CellMouseClick;
            grid.CellDoubleClick += Grid_CellDoubleClick;

            // Intercept user-initiated clicks before the grid processes them.
            // This flag lets SelectionChanged distinguish user vs programmatic changes.
            grid.MouseDown += (s, e) =>
            {
                if (!allowUserSelection)
                    suppressingUserSelection = true;
            };
            grid.MouseUp += (s, e) =>
            {
                suppressingUserSelection = false;
            };
            grid.KeyDown += (s, e) =>
            {
                // Block arrow-key / Shift+Click selection changes when user selection is disabled
                if (!allowUserSelection && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                    e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                    e.KeyCode == Keys.Home || e.KeyCode == Keys.End ||
                    e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            grid.SelectionChanged += (s, e) =>
            {
                // Only block user-initiated selection changes, never programmatic ones.
                if (suppressingUserSelection && !isProgrammaticSelection)
                    RestoreLockedActiveItem();
                else
                    SyncSelectionFromGrid();
            };
            grid.CurrentCellChanged += (s, e) =>
            {
                if (suppressingUserSelection || isProgrammaticSelection) return;
                int rowIndex = grid.CurrentCell?.RowIndex ?? -1;
                if (rowIndex >= 0 && rowIndex < visibleRows.Count && SetActiveItem(visibleRows[rowIndex]))
                    ActiveItemChanged?.Invoke(this, activeItemTag);
            };

            Controls.Add(grid);
        }

        // ---------------------------------------------------------------
        // Column setup  (LabVIEW: Column Headers / Column Widths / Column Order)
        // ---------------------------------------------------------------

        private int columnCount = 1;

        public string[] ColumnHeaders
        {
            get => grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray();
            set
            {
                grid.Columns.Clear();
                columnCount = value.Length;
                foreach (var h in value)
                {
                    grid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = h,
                        Width = 120,
                        SortMode = DataGridViewColumnSortMode.NotSortable
                    });
                }
            }
        }

        public int[] ColumnWidths
        {
            get => grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray();
            set
            {
                for (int i = 0; i < value.Length && i < grid.Columns.Count; i++)
                    grid.Columns[i].Width = value[i];
            }
        }

        public void SetColumnFont(int columnIndex, Font font)
        {
            if (columnIndex >= 0 && columnIndex < grid.Columns.Count)
            {
                grid.Columns[columnIndex].DefaultCellStyle.Font = font;
                grid.Invalidate();
            }
        }

        public Font GetColumnFont(int columnIndex)
        {
            if (columnIndex >= 0 && columnIndex < grid.Columns.Count)
            {
                return grid.Columns[columnIndex].DefaultCellStyle.Font ?? grid.Font;
            }
            return grid.Font;
        }

        public void SetColumnAlignment(int columnIndex, DataGridViewContentAlignment alignment)
        {
            if (columnIndex >= 0 && columnIndex < grid.Columns.Count)
            {
                grid.Columns[columnIndex].DefaultCellStyle.Alignment = alignment;
                grid.Invalidate();
            }
        }

        public DataGridViewContentAlignment GetColumnAlignment(int columnIndex)
        {
            if (columnIndex >= 0 && columnIndex < grid.Columns.Count)
            {
                return grid.Columns[columnIndex].DefaultCellStyle.Alignment;
            }
            return DataGridViewContentAlignment.MiddleLeft;
        }

        public Color HeaderBackColor
        {
            get => grid.ColumnHeadersDefaultCellStyle.BackColor;
            set
            {
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = value;
            }
        }

        public Color HeaderForeColor
        {
            get => grid.ColumnHeadersDefaultCellStyle.ForeColor;
            set
            {
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = value;
            }
        }

        /// <summary>Background colour of the header text (LabVIEW colour format).</summary>
        public int HeaderBackColorLV
        {
            get => ColorToLabView(HeaderBackColor);
            set => HeaderBackColor = ColorFromLabView(value);
        }

        /// <summary>Text colour of the header text (LabVIEW colour format).</summary>
        public int HeaderForeColorLV
        {
            get => ColorToLabView(HeaderForeColor);
            set => HeaderForeColor = ColorFromLabView(value);
        }

        public Font HeaderFont
        {
            get => grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
            set => grid.ColumnHeadersDefaultCellStyle.Font = value;
        }

        public int HeaderHeight
        {
            get => grid.ColumnHeadersHeight;
            set
            {
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                grid.ColumnHeadersHeight = value;
            }
        }

        public DataGridViewContentAlignment HeaderAlignment
        {
            get => grid.ColumnHeadersDefaultCellStyle.Alignment;
            set => grid.ColumnHeadersDefaultCellStyle.Alignment = value;
        }

        /// <summary>Background colour of the selected/active row highlight.</summary>
        public Color SelectionBackColor
        {
            get => grid.DefaultCellStyle.SelectionBackColor;
            set => grid.DefaultCellStyle.SelectionBackColor = value;
        }

        /// <summary>Text colour of the selected/active row highlight.</summary>
        public Color SelectionForeColor
        {
            get => grid.DefaultCellStyle.SelectionForeColor;
            set => grid.DefaultCellStyle.SelectionForeColor = value;
        }

        /// <summary>Background colour of the selected/active row highlight (LabVIEW colour format).</summary>
        public int SelectionBackColorLV
        {
            get => ColorToLabView(SelectionBackColor);
            set => SelectionBackColor = ColorFromLabView(value);
        }

        /// <summary>Text colour of the selected/active row highlight (LabVIEW colour format).</summary>
        public int SelectionForeColorLV
        {
            get => ColorToLabView(SelectionForeColor);
            set => SelectionForeColor = ColorFromLabView(value);
        }

        // ---------------------------------------------------------------
        // Bulk update guard - wrap large Add Item.Multiple style loads in this
        // ---------------------------------------------------------------

        public void BeginUpdate()
        {
            updateDepth++;
            if (updateDepth == 1)
                grid.SuspendLayout();
        }

        public void EndUpdate()
        {
            if (updateDepth == 0) return;
            updateDepth--;
            if (updateDepth == 0)
            {
                if (visibleRowsDirty) RebuildVisibleRows();
                grid.ResumeLayout();
                grid.Invalidate();
            }
        }

        // ---------------------------------------------------------------
        // Add Item / Add Item.Multiple
        // ---------------------------------------------------------------

        public LvTreeItem AddItem(string tag, string parentTag, int position, string[] values)
        {
            if (allItems.ContainsKey(tag))
                throw new ArgumentException($"Item with tag '{tag}' already exists.");

            var node = new LvTreeItem(tag, parentTag, columnCount);
            for (int i = 0; i < values.Length && i < columnCount; i++)
                node.CellValues[i] = values[i];

            if (string.IsNullOrEmpty(parentTag))
            {
                node.Level = 0;
                InsertAt(roots, node, position);
            }
            else
            {
                if (!allItems.TryGetValue(parentTag, out var parent))
                    throw new ArgumentException($"Parent tag '{parentTag}' not found.");
                node.Parent = parent;
                node.Level = parent.Level + 1;
                InsertAt(parent.Children, node, position);
            }

            allItems[tag] = node;
            MarkDirtyOrRebuild();
            return node;
        }

        /// <summary>
        /// When true, assigning ActiveItem or ActiveItemTag expands the item's
        /// ancestors and scrolls it into view. The default preserves LabVIEW-like
        /// active-item behavior.
        /// </summary>
        public bool EnsureActiveItemVisible
        {
            get => ensureActiveItemVisible;
            set => ensureActiveItemVisible = value;
        }

        /// <summary>Adds an item after its existing siblings.</summary>
        public LvTreeItem AddItemToEnd(string tag, string parentTag, string[] values, int glyphIndex = 0)
        {
            var node = AddItem(tag, parentTag, -1, values);
            node.GlyphIndex = glyphIndex;
            return node;
        }

        /// <summary>
        /// C#-side convenience overload. Not usable from LabVIEW - .NET interop
        /// can't marshal tuples or IEnumerable&lt;T&gt; across the boundary.
        /// Use the array-based overload below from a VI.
        /// </summary>
        public void AddItemMultiple(IEnumerable<(string tag, string parentTag, int glyphIndex, string[] values)> items)
        {
            BeginUpdate();
            try
            {
                foreach (var it in items)
                    AddItemToEnd(it.tag, it.parentTag, it.values, it.glyphIndex);
            }
            finally { EndUpdate(); }
        }

        /// <summary>
        /// LabVIEW-callable version of Add Multiple Items to End.
        /// Parallel 1D arrays for tag/parentTag/glyphIndex, plus one 2D array for
        /// cell values (rows = items, columns = your column count). LabVIEW's
        /// 2D array of strings maps directly onto a .NET string[,] via the .NET
        /// Constructor/Invoke Node, so this is the shape to build on the block
        /// diagram - no need to construct .NET objects or arrays-of-arrays.
        ///
        /// All inputs must agree on row count:
        ///   tags.Length == parentTags.Length == glyphIndices.Length == values.GetLength(0)
        /// parentTags: use "" (not null) for a root item - LabVIEW string
        /// controls/arrays can't carry a null.
        /// glyphIndices: use 0 for no glyph; other values select GlyphImages.Images.
        /// Items are appended after their existing siblings in the supplied order.
        /// </summary>
        public void AddItemMultiple(string[] tags, string[] parentTags, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            AddItemMultiple(tags, parentTags, Enumerable.Repeat(0, tags.Length).ToArray(), values);
        }

        /// <summary>
        /// Adds multiple items after their existing siblings in the supplied order,
        /// assigning an optional first-column glyph to each item.
        /// </summary>
        public void AddItemMultiple(string[] tags, string[] parentTags, int[] glyphIndices, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (parentTags == null) throw new ArgumentNullException(nameof(parentTags));
            if (glyphIndices == null) throw new ArgumentNullException(nameof(glyphIndices));
            if (values == null) throw new ArgumentNullException(nameof(values));
            int n = tags.Length;
            if (parentTags.Length != n || glyphIndices.Length != n || values.GetLength(0) != n)
                throw new ArgumentException(
                    $"Array length mismatch: tags={n}, parentTags={parentTags.Length}, " +
                    $"glyphIndices={glyphIndices.Length}, values rows={values.GetLength(0)}. " +
                    "All must match.");

            int cols = values.GetLength(1);

            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var rowValues = new string[cols];
                    for (int c = 0; c < cols; c++)
                        rowValues[c] = values[i, c];

                    string parentTag = string.IsNullOrEmpty(parentTags[i]) ? null : parentTags[i];
                    AddItemToEnd(tags[i], parentTag, rowValues, glyphIndices[i]);
                }
            }
            finally { EndUpdate(); }
        }

        /// <summary>
        /// Bulk updates cell values for multiple items, starting at the specified column index.
        /// Parallel 1D array of tags, start column index, and 2D array of string values
        /// (rows = items matching the tags, columns = new cell values starting from columnStartIndex).
        /// </summary>
        public void UpdateItemMultiple(string[] tags, int columnStartIndex, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (values == null) throw new ArgumentNullException(nameof(values));
            int n = tags.Length;
            if (values.GetLength(0) != n)
                throw new ArgumentException(
                    $"Array length mismatch: tags={n}, values rows={values.GetLength(0)}.");

            int cols = values.GetLength(1);

            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string tag = tags[i];
                    if (string.IsNullOrEmpty(tag)) continue;

                    if (allItems.TryGetValue(tag, out var node))
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            int targetCol = columnStartIndex + c;
                            if (targetCol >= 0 && targetCol < node.CellValues.Length)
                            {
                                node.CellValues[targetCol] = values[i, c];
                            }
                        }
                        InvalidateRowIfVisible(node);
                    }
                }
            }
            finally { EndUpdate(); }
        }

        private static void InsertAt(List<LvTreeItem> siblings, LvTreeItem node, int position)
        {
            if (position < 0 || position >= siblings.Count)
                siblings.Add(node);
            else
                siblings.Insert(position, node);
        }

        // ---------------------------------------------------------------
        // Remove Item / Remove Item.Multiple / Remove Item.All
        // ---------------------------------------------------------------

        public void RemoveItem(string tag)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;

            if (string.Equals(activeItemTag, tag, StringComparison.Ordinal))
                SetActiveItem(null);

            // recursively drop children first
            foreach (var child in node.Children.ToArray())
                RemoveItem(child.Tag);

            var siblings = node.Parent?.Children ?? roots;
            siblings.Remove(node);
            allItems.Remove(tag);
            MarkDirtyOrRebuild();
        }

        public void RemoveItemMultiple(IEnumerable<string> tags)
        {
            BeginUpdate();
            try { foreach (var t in tags.ToArray()) RemoveItem(t); }
            finally { EndUpdate(); }
        }

        public void RemoveItemAll()
        {
            SetActiveItem(null);
            selectedTags.Clear();
            allItems.Clear();
            roots.Clear();
            visibleRows.Clear();
            grid.RowCount = 0;
            grid.Invalidate();
        }

        // ---------------------------------------------------------------
        // Move Item
        // ---------------------------------------------------------------

        public void MoveItem(string tag, string newParentTag, int position)
        {
            if (!allItems.TryGetValue(tag, out var node))
                throw new ArgumentException($"Tag '{tag}' not found.");

            var oldSiblings = node.Parent?.Children ?? roots;
            oldSiblings.Remove(node);

            if (string.IsNullOrEmpty(newParentTag))
            {
                node.Parent = null;
                node.Level = 0;
                InsertAt(roots, node, position);
            }
            else
            {
                if (!allItems.TryGetValue(newParentTag, out var newParent))
                    throw new ArgumentException($"Parent tag '{newParentTag}' not found.");
                node.Parent = newParent;
                node.Level = newParent.Level + 1;
                InsertAt(newParent.Children, node, position);
            }

            RenumberLevels(node);
            MarkDirtyOrRebuild();
        }

        private void RenumberLevels(LvTreeItem node)
        {
            foreach (var c in node.Children)
            {
                c.Level = node.Level + 1;
                RenumberLevels(c);
            }
        }

        // ---------------------------------------------------------------
        // Item.Set Open  /  Item Exists  /  Get Item Reference
        // ---------------------------------------------------------------

        public void ItemSetOpen(string tag, bool open)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            if (node.IsOpen == open) return;
            node.IsOpen = open;
            ItemOpenedClosed?.Invoke(this, tag);
            MarkDirtyOrRebuild();
        }

        public bool ItemExists(string tag) => allItems.ContainsKey(tag);

        public LvTreeItem GetItemReference(string tag) =>
            allItems.TryGetValue(tag, out var node) ? node : null;

        // ---------------------------------------------------------------
        // Point To Row / Point To Column  (hit testing, e.g. for click handling)
        // ---------------------------------------------------------------

        public string PointToRow(Point clientPoint)
        {
            var hit = grid.HitTest(clientPoint.X, clientPoint.Y);
            if (hit.RowIndex < 0 || hit.RowIndex >= visibleRows.Count) return null;
            return visibleRows[hit.RowIndex].Tag;
        }

        public int PointToColumn(Point clientPoint)
        {
            var hit = grid.HitTest(clientPoint.X, clientPoint.Y);
            return hit.ColumnIndex;
        }

        // ---------------------------------------------------------------
        // Scroll To Item / Scroll To Row / Top Item
        // ---------------------------------------------------------------

        public void ScrollToItem(string tag)
        {
            var idx = visibleRows.FindIndex(n => n.Tag == tag);
            if (idx >= 0) ScrollToRow(idx);
        }

        public void ScrollToRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.RowCount) return;
            grid.FirstDisplayedScrollingRowIndex = rowIndex;
        }

        public int TopItem
        {
            get => grid.FirstDisplayedScrollingRowIndex;
            set => grid.FirstDisplayedScrollingRowIndex = value;
        }

        // ---------------------------------------------------------------
        // Sort Items
        // ---------------------------------------------------------------

        public void SortItems(int columnIndex, bool ascending, Comparison<LvTreeItem> customComparer = null)
        {
            Comparison<LvTreeItem> cmp = customComparer ?? ((a, b) =>
            {
                var va = a.CellValues.Length > columnIndex ? a.CellValues[columnIndex] : null;
                var vb = b.CellValues.Length > columnIndex ? b.CellValues[columnIndex] : null;
                int r = Comparer<object>.Default.Compare(va, vb);
                return ascending ? r : -r;
            });

            void SortLevel(List<LvTreeItem> siblings)
            {
                siblings.Sort(cmp);
                foreach (var s in siblings) SortLevel(s.Children);
            }

            SortLevel(roots);
            MarkDirtyOrRebuild();
        }

        // ---------------------------------------------------------------
        // Edit Active Item
        // ---------------------------------------------------------------

        public void EditActiveItem()
        {
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            if (grid.CurrentCell != null)
                grid.BeginEdit(true);
        }

        // ---------------------------------------------------------------
        // Items[] / Active Item / Selected Items[]
        // ---------------------------------------------------------------

        /// <summary>Flat snapshot of every item, depth-first (same ordering LabVIEW's Items[] uses).</summary>
        public LvTreeItem[] Items
        {
            get
            {
                var result = new List<LvTreeItem>();
                void Walk(List<LvTreeItem> siblings)
                {
                    foreach (var s in siblings)
                    {
                        result.Add(s);
                        Walk(s.Children);
                    }
                }
                Walk(roots);
                return result.ToArray();
            }
        }

        public void EnsureVisible(string tag)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            bool changed = false;
            var parent = node.Parent;
            while (parent != null)
            {
                if (!parent.IsOpen)
                {
                    parent.IsOpen = true;
                    changed = true;
                }
                parent = parent.Parent;
            }
            if (changed)
            {
                MarkDirtyOrRebuild();
            }
        }

        public LvTreeItem ActiveItem
        {
            get => !string.IsNullOrEmpty(activeItemTag) && allItems.TryGetValue(activeItemTag, out var node)
                ? node
                : null;
            set
            {
                if (value == null) return;
                bool changed = SetActiveItem(value);
                if (ensureActiveItemVisible)
                    EnsureVisible(value.Tag);
                var idx = visibleRows.FindIndex(n => n.Tag == value.Tag);

                // Only scroll if the row is outside the current visible viewport.
                // Scrolling unconditionally forces the row to the top and triggers
                // a full repaint even when the row is already on screen.
                int firstVisible = grid.FirstDisplayedScrollingRowIndex;
                int visibleCount = grid.DisplayedRowCount(false);
                bool isVisible = firstVisible >= 0
                              && idx >= firstVisible
                              && idx < firstVisible + visibleCount;
                if (ensureActiveItemVisible && idx >= 0 && !isVisible)
                    grid.FirstDisplayedScrollingRowIndex = idx;

                isProgrammaticSelection = true;
                try
                {
                    // Clear the prior visible selection even if the new active item
                    // is hidden or off-screen. Its Active state will paint when shown.
                    grid.ClearSelection();
                    if (idx >= 0 && grid.Columns.Count > 0 && (ensureActiveItemVisible || isVisible))
                        grid.CurrentCell = grid.Rows[idx].Cells[0];
                    if (!allowUserSelection)
                        lockedActiveItemTag = value.Tag;
                }
                finally { isProgrammaticSelection = false; }

                if (changed)
                    ActiveItemChanged?.Invoke(this, activeItemTag);
            }
        }

        public string ActiveItemTag
        {
            get => ActiveItem?.Tag;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    bool changed = SetActiveItem(null);
                    isProgrammaticSelection = true;
                    try
                    {
                        grid.ClearSelection();
                        grid.CurrentCell = null;
                    }
                    finally { isProgrammaticSelection = false; }
                    if (!allowUserSelection)
                        lockedActiveItemTag = null;
                    if (changed)
                        ActiveItemChanged?.Invoke(this, null);
                }
                else if (allItems.TryGetValue(value, out var node))
                {
                    ActiveItem = node;
                }
            }
        }

        public int ActiveItemIndex => string.IsNullOrEmpty(activeItemTag)
            ? -1
            : visibleRows.FindIndex(n => n.Tag == activeItemTag);

        private readonly HashSet<string> selectedTags = new HashSet<string>();

        public LvTreeItem[] SelectedItems => selectedTags
            .Where(allItems.ContainsKey)
            .Select(t => allItems[t])
            .ToArray();

        public string[] SelectedItemTags => selectedTags.ToArray();

        private void SyncSelectionFromGrid()
        {
            selectedTags.Clear();
            foreach (DataGridViewCell c in grid.SelectedCells)
            {
                if (c.RowIndex >= 0 && c.RowIndex < visibleRows.Count)
                    selectedTags.Add(visibleRows[c.RowIndex].Tag);
            }
            foreach (var n in allItems.Values) n.Selected = selectedTags.Contains(n.Tag);
        }

        private bool SetActiveItem(LvTreeItem node)
        {
            string newTag = node?.Tag;
            if (string.Equals(activeItemTag, newTag, StringComparison.Ordinal)) return false;

            if (!string.IsNullOrEmpty(activeItemTag) && allItems.TryGetValue(activeItemTag, out var oldNode))
            {
                oldNode.Active = false;
                InvalidateRowIfVisible(oldNode);
            }

            activeItemTag = newTag;
            if (node != null)
            {
                node.Active = true;
                InvalidateRowIfVisible(node);
            }
            return true;
        }

        private void RestoreLockedActiveItem()
        {
            isProgrammaticSelection = true;
            try
            {
                grid.ClearSelection();
                if (!string.IsNullOrEmpty(lockedActiveItemTag))
                {
                    int index = visibleRows.FindIndex(n => n.Tag == lockedActiveItemTag);
                    grid.CurrentCell = index >= 0 && grid.Columns.Count > 0
                        ? grid.Rows[index].Cells[0]
                        : null;
                }
                else
                {
                    grid.CurrentCell = null;
                }
            }
            finally
            {
                isProgrammaticSelection = false;
            }
        }

        // ---------------------------------------------------------------
        // Colour API - the reason you're moving off native Tree.
        // Whole-row and per-cell colour, both O(1), no property-node-per-item cost.
        // ---------------------------------------------------------------

        public void SetItemFillColor(string tag, Color color)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            node.RowBackColor = color;
            InvalidateRowIfVisible(node);
        }

        /// <summary>
        /// Sets the optional first-column glyph for an item. Use 0 to remove it.
        /// Positive values select an image in <see cref="GlyphImages"/>.
        /// </summary>
        public void SetItemGlyphIndex(string tag, int glyphIndex)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            node.GlyphIndex = glyphIndex;
            InvalidateRowIfVisible(node);
        }

        public void SetCellColor(string tag, int column, Color backColor, Color? foreColor = null)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            if (column < 0 || column >= node.CellBackColors.Length) return;
            node.CellBackColors[column] = backColor;
            if (foreColor.HasValue) node.CellForeColors[column] = foreColor.Value;
            InvalidateRowIfVisible(node);
        }

        public Color GetCellColor(string tag, int column)
        {
            if (!allItems.TryGetValue(tag, out var node)) return Color.Empty;
            if (column < 0 || column >= node.CellBackColors.Length) return Color.Empty;
            return node.CellBackColors[column];
        }

        /// <summary>
        /// Bulk updates background and/or foreground colors for multiple cells starting from the specified column index.
        /// If either backColors or foreColors are null/empty, that set of colors will not be updated.
        /// </summary>
        public void UpdateCellColorsMultiple(string[] tags, int columnStartIndex, Color[,] backColors, Color[,] foreColors)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            int n = tags.Length;

            bool hasBack = backColors != null && backColors.Length > 0;
            bool hasFore = foreColors != null && foreColors.Length > 0;

            if (hasBack && backColors.GetLength(0) != n)
                throw new ArgumentException($"Array length mismatch: tags={n}, backColors rows={backColors.GetLength(0)}.");
            if (hasFore && foreColors.GetLength(0) != n)
                throw new ArgumentException($"Array length mismatch: tags={n}, foreColors rows={foreColors.GetLength(0)}.");

            int backCols = hasBack ? backColors.GetLength(1) : 0;
            int foreCols = hasFore ? foreColors.GetLength(1) : 0;

            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string tag = tags[i];
                    if (string.IsNullOrEmpty(tag)) continue;

                    if (allItems.TryGetValue(tag, out var node))
                    {
                        if (hasBack)
                        {
                            for (int c = 0; c < backCols; c++)
                            {
                                int targetCol = columnStartIndex + c;
                                if (targetCol >= 0 && targetCol < node.CellBackColors.Length)
                                    node.CellBackColors[targetCol] = backColors[i, c];
                            }
                        }

                        if (hasFore)
                        {
                            for (int c = 0; c < foreCols; c++)
                            {
                                int targetCol = columnStartIndex + c;
                                if (targetCol >= 0 && targetCol < node.CellForeColors.Length)
                                    node.CellForeColors[targetCol] = foreColors[i, c];
                            }
                        }

                        InvalidateRowIfVisible(node);
                    }
                }
            }
            finally { EndUpdate(); }
        }

        /// <summary>
        /// Bulk updates background colors for multiple cells starting from the specified column index.
        /// </summary>
        public void UpdateCellBackColorsMultiple(string[] tags, int columnStartIndex, Color[,] backColors)
        {
            UpdateCellColorsMultiple(tags, columnStartIndex, backColors, null);
        }

        /// <summary>
        /// Bulk updates foreground colors for multiple cells starting from the specified column index.
        /// </summary>
        public void UpdateCellForeColorsMultiple(string[] tags, int columnStartIndex, Color[,] foreColors)
        {
            UpdateCellColorsMultiple(tags, columnStartIndex, null, foreColors);
        }

        // ---------------------------------------------------------------
        // LabVIEW Color Box (U32/I32 integer color representation) Overloads
        // ---------------------------------------------------------------

        private static int ColorToLabView(Color lvColor)
        {
            if (lvColor == Color.Transparent)
            {
                return 0x01000000;
            }
            return (int)((lvColor.R << 16) | (lvColor.G << 8) | lvColor.B);
        }

        private static Color ColorFromLabView(int lvColor)
        {
            if (lvColor == 0x01000000)
            {
                return Color.Transparent;
            }
            int r = (lvColor >> 16) & 0xFF;
            int g = (lvColor >> 8) & 0xFF;
            int b = lvColor & 0xFF;
            return Color.FromArgb(255, r, g, b);
        }

        public void SetItemFillColor(string tag, int lvColor)
        {
            SetItemFillColor(tag, ColorFromLabView(lvColor));
        }

        public void SetCellColor(string tag, int column, int lvBackColor)
        {
            SetCellColor(tag, column, ColorFromLabView(lvBackColor), null);
        }

        public void SetCellColor(string tag, int column, int lvBackColor, int lvForeColor)
        {
            SetCellColor(tag, column, ColorFromLabView(lvBackColor), ColorFromLabView(lvForeColor));
        }

        public void UpdateCellColorsMultiple(string[] tags, int columnStartIndex, int[,] lvBackColors, int[,] lvForeColors)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            int n = tags.Length;

            bool hasBack = lvBackColors != null && lvBackColors.Length > 0;
            bool hasFore = lvForeColors != null && lvForeColors.Length > 0;

            Color[,] backColors = null;
            if (hasBack)
            {
                int rows = lvBackColors.GetLength(0);
                int cols = lvBackColors.GetLength(1);
                backColors = new Color[rows, cols];
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        backColors[r, c] = ColorFromLabView(lvBackColors[r, c]);
                    }
                }
            }

            Color[,] foreColors = null;
            if (hasFore)
            {
                int rows = lvForeColors.GetLength(0);
                int cols = lvForeColors.GetLength(1);
                foreColors = new Color[rows, cols];
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        foreColors[r, c] = ColorFromLabView(lvForeColors[r, c]);
                    }
                }
            }

            UpdateCellColorsMultiple(tags, columnStartIndex, backColors, foreColors);
        }

        public void UpdateCellBackColorsMultiple(string[] tags, int columnStartIndex, int[,] lvBackColors)
        {
            UpdateCellColorsMultiple(tags, columnStartIndex, lvBackColors, null);
        }

        public void UpdateCellForeColorsMultiple(string[] tags, int columnStartIndex, int[,] lvForeColors)
        {
            UpdateCellColorsMultiple(tags, columnStartIndex, null, lvForeColors);
        }


        /// <summary>
        /// Colors multiple cells the same background and/or foreground color, specified by parallel arrays of tags and column indices.
        /// </summary>
        public void SetCellsColor(string[] tags, int[] columns, Color backColor, Color? foreColor = null)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (columns == null) throw new ArgumentNullException(nameof(columns));
            int n = tags.Length;
            if (columns.Length != n)
                throw new ArgumentException($"Array length mismatch: tags={n}, columns={columns.Length}.");

            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string tag = tags[i];
                    if (string.IsNullOrEmpty(tag)) continue;

                    if (allItems.TryGetValue(tag, out var node))
                    {
                        int col = columns[i];
                        if (col >= 0 && col < node.CellBackColors.Length)
                        {
                            node.CellBackColors[col] = backColor;
                            if (foreColor.HasValue)
                                node.CellForeColors[col] = foreColor.Value;
                        }
                        InvalidateRowIfVisible(node);
                    }
                }
            }
            finally { EndUpdate(); }
        }

        public void SetCellsColor(string[] tags, int[] columns, int lvBackColor)
        {
            SetCellsColor(tags, columns, ColorFromLabView(lvBackColor), null);
        }

        public void SetCellsColor(string[] tags, int[] columns, int lvBackColor, int lvForeColor)
        {
            SetCellsColor(tags, columns, ColorFromLabView(lvBackColor), ColorFromLabView(lvForeColor));
        }

        public void ExpandAll()
        {
            BeginUpdate();
            try
            {
                foreach (var node in allItems.Values)
                {
                    node.IsOpen = true;
                }
            }
            finally { EndUpdate(); }
        }

        public void CollapseAll()
        {
            BeginUpdate();
            try
            {
                foreach (var node in allItems.Values)
                {
                    node.IsOpen = false;
                }
            }
            finally { EndUpdate(); }
        }

        public void AutoFitColumns()
        {
            using (var g = grid.CreateGraphics())
            {
                for (int col = 0; col < grid.Columns.Count; col++)
                {
                    int maxWidth = 0;

                    // Measure header text width
                    var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
                    var headerText = grid.Columns[col].HeaderText;
                    var headerSize = TextRenderer.MeasureText(g, headerText, headerFont);
                    maxWidth = Math.Max(maxWidth, headerSize.Width + 10);

                    // Get column font
                    var colFont = grid.Columns[col].DefaultCellStyle.Font ?? grid.Font;

                    foreach (var node in allItems.Values)
                    {
                        var valStr = node.CellValues.Length > col ? node.CellValues[col]?.ToString() : "";
                        int width = string.IsNullOrEmpty(valStr) ? 0 : TextRenderer.MeasureText(g, valStr, colFont).Width + 10;

                        if (col == 0)
                        {
                            int indent = node.Level * IndentPx;
                            Size glyphSize = GetGlyphSize(g, node.IsOpen);
                            width += indent + glyphSize.Width + 8;
                            if (HasGlyph(node))
                                width += glyphImages.ImageSize.Width + 4;
                        }

                        if (width > maxWidth)
                        {
                            maxWidth = width;
                        }
                    }

                    if (maxWidth > 0)
                    {
                        grid.Columns[col].Width = Math.Max(maxWidth, 40);
                    }
                }
            }
        }

        public void ClearAllCellColors()
        {
            BeginUpdate();
            try
            {
                foreach (var node in allItems.Values)
                {
                    node.RowBackColor = Color.Empty;
                    for (int i = 0; i < node.CellBackColors.Length; i++)
                    {
                        node.CellBackColors[i] = Color.Empty;
                        node.CellForeColors[i] = Color.Empty;
                    }
                }
            }
            finally { EndUpdate(); }
        }

        private void InvalidateRowIfVisible(LvTreeItem node)
        {
            // Skip the search cost during bulk loads - EndUpdate repaints everything anyway.
            if (updateDepth > 0) return;
            var idx = visibleRows.IndexOf(node);
            if (idx >= 0) grid.InvalidateRow(idx);
        }

        // ---------------------------------------------------------------
        // Internal: flatten hierarchy into visibleRows, respecting IsOpen
        // ---------------------------------------------------------------

        private void MarkDirtyOrRebuild()
        {
            if (updateDepth > 0) { visibleRowsDirty = true; return; }
            RebuildVisibleRows();
        }

        private void RebuildVisibleRows()
        {
            visibleRowsDirty = false;
            visibleRows = new List<LvTreeItem>(allItems.Count);

            void Walk(List<LvTreeItem> siblings)
            {
                foreach (var s in siblings)
                {
                    visibleRows.Add(s);
                    if (s.IsOpen && s.HasChildren)
                        Walk(s.Children);
                }
            }
            Walk(roots);

            grid.RowCount = visibleRows.Count;
            grid.Invalidate();
        }

        // ---------------------------------------------------------------
        // Grid callbacks - only ever touch the handful of rows on screen
        // ---------------------------------------------------------------

        private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count) return;
            var node = visibleRows[e.RowIndex];
            e.Value = (e.ColumnIndex >= 0 && e.ColumnIndex < node.CellValues.Length)
                ? node.CellValues[e.ColumnIndex]
                : null;
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex < 0)
                return;

            var node = visibleRows[e.RowIndex];
            Color back = (node.CellBackColors[e.ColumnIndex] != Color.Empty)
                ? node.CellBackColors[e.ColumnIndex]
                : (node.RowBackColor != Color.Empty ? node.RowBackColor : e.CellStyle.BackColor);
            Color fore = (node.CellForeColors[e.ColumnIndex] != Color.Empty)
                ? node.CellForeColors[e.ColumnIndex]
                : e.CellStyle.ForeColor;

            // Custom painting bypasses DataGridView's normal selection rendering,
            // so apply the inherited selection colours explicitly. This preserves
            // SelectionBackColor/SelectionForeColor set on the control or column.
            if (node.Selected || node.Active)
            {
                back = e.CellStyle.SelectionBackColor;
                fore = e.CellStyle.SelectionForeColor;
            }

            using (var backBrush = new SolidBrush(back))
                e.Graphics.FillRectangle(backBrush, e.CellBounds);

            int textLeft = e.CellBounds.Left + 4;

            // First column: draw indent + expand/collapse glyph before the text.
            if (e.ColumnIndex == 0)
            {
                int indent = node.Level * IndentPx;
                textLeft += indent;

                if (node.HasChildren)
                {
                    Size glyphSize = GetGlyphSize(e.Graphics, node.IsOpen);
                    int gx = e.CellBounds.Left + indent;
                    int gy = e.CellBounds.Top + (e.CellBounds.Height - glyphSize.Height) / 2;
                    Rectangle glyphRect = new Rectangle(gx, gy, glyphSize.Width, glyphSize.Height);

                    bool drawn = false;
                    if (Application.RenderWithVisualStyles)
                    {
                        VisualStyleElement element = node.IsOpen
                            ? VisualStyleElement.TreeView.Glyph.Opened
                            : VisualStyleElement.TreeView.Glyph.Closed;
                        if (VisualStyleRenderer.IsElementDefined(element))
                        {
                            var renderer = new VisualStyleRenderer(element);
                            renderer.DrawBackground(e.Graphics, glyphRect);
                            drawn = true;
                        }
                    }

                    if (!drawn)
                    {
                        using (var pen = new Pen(SystemColors.ControlDarkDark))
                            e.Graphics.DrawRectangle(pen, glyphRect);
                        int midY = gy + glyphSize.Height / 2;
                        e.Graphics.DrawLine(Pens.Black, gx + 2, midY, gx + glyphSize.Width - 2, midY);
                        if (!node.IsOpen)
                            e.Graphics.DrawLine(Pens.Black, gx + glyphSize.Width / 2, gy + 2, gx + glyphSize.Width / 2, gy + glyphSize.Height - 2);
                    }

                    textLeft += glyphSize.Width + 4;
                }
                else
                {
                    Size glyphSize = GetGlyphSize(e.Graphics, false);
                    textLeft += glyphSize.Width + 4;
                }

                if (HasGlyph(node))
                {
                    Image glyph = glyphImages.Images[node.GlyphIndex];
                    Size glyphSize = glyphImages.ImageSize;
                    int glyphTop = e.CellBounds.Top + (e.CellBounds.Height - glyphSize.Height) / 2;
                    e.Graphics.DrawImage(glyph, new Rectangle(textLeft, glyphTop, glyphSize.Width, glyphSize.Height));
                    textLeft += glyphSize.Width + 4;
                }
            }

            var text = node.CellValues.Length > e.ColumnIndex ? node.CellValues[e.ColumnIndex]?.ToString() : "";
            var flags = GetTextFormatFlags(e.CellStyle.Alignment);
            TextRenderer.DrawText(
                e.Graphics, text, e.CellStyle.Font,
                new Rectangle(textLeft, e.CellBounds.Top, e.CellBounds.Right - textLeft, e.CellBounds.Height),
                fore, flags);

            e.Handled = true;
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex != 0) return;
            var node = visibleRows[e.RowIndex];
            if (!node.HasChildren) return;

            // Only toggle if the click landed on the actual expand/collapse glyph.
            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Size glyphSize;
            using (var g = grid.CreateGraphics())
            {
                glyphSize = GetGlyphSize(g, node.IsOpen);
            }
            var glyphRect = new Rectangle(
                cellRect.Left + node.Level * IndentPx,
                cellRect.Top + (cellRect.Height - glyphSize.Height) / 2,
                glyphSize.Width,
                glyphSize.Height);
            glyphRect.Inflate(3, 3);
            // WinForms reports these coordinates relative to the grid on most
            // versions, but some hosts forward them relative to the cell.
            Point gridPoint = new Point(e.X, e.Y);
            Point cellPoint = new Point(cellRect.Left + e.X, cellRect.Top + e.Y);
            if (glyphRect.Contains(gridPoint) || glyphRect.Contains(cellPoint))
                ItemSetOpen(node.Tag, !node.IsOpen);
        }

        private void Grid_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex < 0) return;
            var node = visibleRows[e.RowIndex];
            CellClicked?.Invoke(this, new LvTreeCellClickedEventArgs(
                node.Tag,
                e.ColumnIndex,
                IsGlyphOrExpanderHit(node, e)));
        }

        private bool IsGlyphOrExpanderHit(LvTreeItem node, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != 0) return false;

            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Size expanderSize;
            using (var graphics = grid.CreateGraphics())
            {
                expanderSize = GetGlyphSize(graphics, node.IsOpen);
            }

            int left = cellRect.Left + node.Level * IndentPx;
            if (node.HasChildren)
            {
                var expanderRect = new Rectangle(
                    left,
                    cellRect.Top + (cellRect.Height - expanderSize.Height) / 2,
                    expanderSize.Width,
                    expanderSize.Height);
                expanderRect.Inflate(3, 3);
                if (IsMousePointInRectangle(e, cellRect, expanderRect)) return true;
            }
            left += expanderSize.Width + 4;

            if (!HasGlyph(node)) return false;
            Size itemGlyphSize = glyphImages.ImageSize;
            var itemGlyphRect = new Rectangle(
                left,
                cellRect.Top + (cellRect.Height - itemGlyphSize.Height) / 2,
                itemGlyphSize.Width,
                itemGlyphSize.Height);
            return IsMousePointInRectangle(e, cellRect, itemGlyphRect);
        }

        private static bool IsMousePointInRectangle(
            DataGridViewCellMouseEventArgs e,
            Rectangle cellRect,
            Rectangle targetRect)
        {
            Point gridPoint = new Point(e.X, e.Y);
            Point cellPoint = new Point(cellRect.Left + e.X, cellRect.Top + e.Y);
            return targetRect.Contains(gridPoint) || targetRect.Contains(cellPoint);
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count) return;
            ItemDoubleClicked?.Invoke(this, visibleRows[e.RowIndex].Tag);
        }

        private Size GetGlyphSize(Graphics g, bool isOpen)
        {
            if (Application.RenderWithVisualStyles)
            {
                VisualStyleElement element = isOpen
                    ? VisualStyleElement.TreeView.Glyph.Opened
                    : VisualStyleElement.TreeView.Glyph.Closed;

                if (VisualStyleRenderer.IsElementDefined(element))
                {
                    var renderer = new VisualStyleRenderer(element);
                    return renderer.GetPartSize(g, ThemeSizeType.True);
                }
            }
            return new Size(GlyphSize, GlyphSize);
        }

        private static Bitmap DrawCheckBoxGlyph(bool isChecked)
        {
            var bitmap = new Bitmap(16, 16);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var bounds = new Rectangle(2, 2, 11, 11);
                using (var fill = new SolidBrush(isChecked ? Color.FromArgb(0, 120, 215) : Color.White))
                    graphics.FillRectangle(fill, bounds);
                using (var border = new Pen(Color.FromArgb(90, 90, 90)))
                    graphics.DrawRectangle(border, bounds);
                if (isChecked)
                {
                    using (var check = new Pen(Color.White, 2f))
                    {
                        check.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        check.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        graphics.DrawLines(check, new[] { new Point(4, 7), new Point(6, 10), new Point(11, 4) });
                    }
                }
            }
            return bitmap;
        }

        private static Bitmap DrawPowerGlyph(Color color)
        {
            var bitmap = new Bitmap(16, 16);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(color, 1.75f))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                graphics.DrawArc(pen, new Rectangle(4, 5, 8, 8), 35, 290);
                graphics.DrawLine(pen, new Point(8, 3), new Point(8, 8));
            }
            return bitmap;
        }

        private bool HasGlyph(LvTreeItem node) =>
            glyphImages != null && node.GlyphIndex > 0 && node.GlyphIndex < glyphImages.Images.Count;

        private TextFormatFlags GetTextFormatFlags(DataGridViewContentAlignment alignment)
        {
            TextFormatFlags flags = TextFormatFlags.EndEllipsis;
            switch (alignment)
            {
                case DataGridViewContentAlignment.TopLeft:
                    flags |= TextFormatFlags.Top | TextFormatFlags.Left;
                    break;
                case DataGridViewContentAlignment.TopCenter:
                    flags |= TextFormatFlags.Top | TextFormatFlags.HorizontalCenter;
                    break;
                case DataGridViewContentAlignment.TopRight:
                    flags |= TextFormatFlags.Top | TextFormatFlags.Right;
                    break;
                case DataGridViewContentAlignment.MiddleLeft:
                    flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
                    break;
                case DataGridViewContentAlignment.MiddleCenter:
                    flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
                    break;
                case DataGridViewContentAlignment.MiddleRight:
                    flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
                    break;
                case DataGridViewContentAlignment.BottomLeft:
                    flags |= TextFormatFlags.Bottom | TextFormatFlags.Left;
                    break;
                case DataGridViewContentAlignment.BottomCenter:
                    flags |= TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter;
                    break;
                case DataGridViewContentAlignment.BottomRight:
                    flags |= TextFormatFlags.Bottom | TextFormatFlags.Right;
                    break;
                default:
                    flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
                    break;
            }
            return flags;
        }
    }
}
