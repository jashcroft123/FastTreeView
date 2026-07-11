using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace LvControls
{
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

        private const int IndentPx = 16;
        private const int GlyphSize = 9;

        public event EventHandler<string> ActiveItemChanged;
        public event EventHandler<string> ItemDoubleClicked;
        public event EventHandler<string> ItemOpenedClosed;

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

            // WinForms deliberately hides DoubleBuffered on controls that don't
            // expose it publicly. DataGridView has heavy default flicker on
            // scroll/repaint at this row count without this.
            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, grid, new object[] { true });

            grid.CellValueNeeded += Grid_CellValueNeeded;
            grid.CellPainting += Grid_CellPainting;
            grid.CellMouseClick += Grid_CellMouseClick;
            grid.CellDoubleClick += Grid_CellDoubleClick;
            grid.SelectionChanged += (s, e) => SyncSelectionFromGrid();
            grid.CurrentCellChanged += (s, e) => ActiveItemChanged?.Invoke(this, ActiveItem.Tag);

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
        /// C#-side convenience overload. Not usable from LabVIEW - .NET interop
        /// can't marshal tuples or IEnumerable&lt;T&gt; across the boundary.
        /// Use the array-based overload below from a VI.
        /// </summary>
        public void AddItemMultiple(IEnumerable<(string tag, string parentTag, int position, string[] values)> items)
        {
            BeginUpdate();
            try
            {
                foreach (var it in items)
                    AddItem(it.tag, it.parentTag, it.position, it.values);
            }
            finally { EndUpdate(); }
        }

        /// <summary>
        /// LabVIEW-callable version of Add Item.Multiple.
        /// Parallel 1D arrays for tag/parentTag/position, plus one 2D array for
        /// cell values (rows = items, columns = your column count). LabVIEW's
        /// 2D array of strings maps directly onto a .NET string[,] via the .NET
        /// Constructor/Invoke Node, so this is the shape to build on the block
        /// diagram - no need to construct .NET objects or arrays-of-arrays.
        ///
        /// All four/five inputs must agree on row count:
        ///   tags.Length == parentTags.Length == positions.Length == values.GetLength(0)
        /// parentTags: use "" (not null) for a root item - LabVIEW string
        /// controls/arrays can't carry a null.
        /// </summary>
        public void AddItemMultiple(string[] tags, string[] parentTags, int[] positions, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            int n = tags.Length;
            if (parentTags.Length != n || positions.Length != n || values.GetLength(0) != n)
                throw new ArgumentException(
                    $"Array length mismatch: tags={n}, parentTags={parentTags.Length}, " +
                    $"positions={positions.Length}, values rows={values.GetLength(0)}. " +
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
                    AddItem(tags[i], parentTag, positions[i], rowValues);
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

        public LvTreeItem ActiveItem
        {
            get
            {
                var r = grid.CurrentCell?.RowIndex ?? -1;
                return (r >= 0 && r < visibleRows.Count) ? visibleRows[r] : null;
            }
            set
            {
                if (value == null) return;
                ScrollToItem(value.Tag);
                var idx = visibleRows.FindIndex(n => n.Tag == value.Tag);
                if (idx >= 0 && grid.Columns.Count > 0)
                    grid.CurrentCell = grid.Rows[idx].Cells[0];
            }
        }

        public string ActiveItemTag => ActiveItem?.Tag;

        public int ActiveItemIndex => grid.CurrentCell?.RowIndex ?? -1;

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

            if (node.Selected) back = ControlPaint.Light(SystemColors.Highlight, 0.4f);

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
                    int gx = e.CellBounds.Left + indent;
                    int gy = e.CellBounds.Top + (e.CellBounds.Height - GlyphSize) / 2;
                    var glyphRect = new Rectangle(gx, gy, GlyphSize, GlyphSize);
                    using (var pen = new Pen(SystemColors.ControlDarkDark))
                        e.Graphics.DrawRectangle(pen, glyphRect);
                    int midY = gy + GlyphSize / 2;
                    e.Graphics.DrawLine(Pens.Black, gx + 2, midY, gx + GlyphSize - 2, midY);
                    if (!node.IsOpen)
                        e.Graphics.DrawLine(Pens.Black, gx + GlyphSize / 2, gy + 2, gx + GlyphSize / 2, gy + GlyphSize - 2);
                    textLeft += GlyphSize + 4;
                }
                else
                {
                    textLeft += GlyphSize + 4;
                }
            }

            var text = node.CellValues.Length > e.ColumnIndex ? node.CellValues[e.ColumnIndex]?.ToString() : "";
            TextRenderer.DrawText(
                e.Graphics, text, e.CellStyle.Font,
                new Rectangle(textLeft, e.CellBounds.Top, e.CellBounds.Right - textLeft, e.CellBounds.Height),
                fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            e.Handled = true;
        }

        private void Grid_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex != 0) return;
            var node = visibleRows[e.RowIndex];
            if (!node.HasChildren) return;

            // Only toggle if the click landed on/near the glyph, not the label text.
            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            int glyphRight = cellRect.Left + node.Level * IndentPx + GlyphSize + 6;
            if (e.Location.X <= glyphRight)
                ItemSetOpen(node.Tag, !node.IsOpen);
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count) return;
            ItemDoubleClicked?.Invoke(this, visibleRows[e.RowIndex].Tag);
        }
    }
}