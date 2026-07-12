using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace LvControls
{
    public enum LvTreeGlyph
    {
        None = 0,
        Unchecked = 1,
        Checked = 2,
        Startup = 3,
        Shutdown = 4
    }

    public sealed class LvTreeCellClickedEventArgs : EventArgs
    {
        public string Tag { get; }
        public int ColumnIndex { get; }
        public bool IsGlyph { get; }
        public bool IsExpander { get; }
        public bool IsGlyphOrExpander => IsGlyph || IsExpander;

        public LvTreeCellClickedEventArgs(string tag, int columnIndex, bool isGlyph, bool isExpander)
        {
            Tag = tag;
            ColumnIndex = columnIndex;
            IsGlyph = isGlyph;
            IsExpander = isExpander;
        }

        public LvTreeCellClickedEventArgs(string tag, int columnIndex, bool isGlyphOrExpander)
            : this(tag, columnIndex, isGlyphOrExpander, isGlyphOrExpander)
        {
        }
    }

    public class LvTreeItem
    {
        public string Tag;
        public string ParentTag;
        public int Level;
        public bool IsOpen = true;
        public object[] CellValues;
        public Color[] CellBackColors;
        public Color[] CellForeColors;
        public Color RowBackColor = Color.Empty;
        public bool Selected;
        public bool Active;
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

    public class LvTreeGrid : UserControl
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly Dictionary<string, LvTreeItem> allItems = new Dictionary<string, LvTreeItem>(StringComparer.Ordinal);
        private readonly List<LvTreeItem> roots = new List<LvTreeItem>();
        private List<LvTreeItem> visibleRows = new List<LvTreeItem>();
        private readonly Dictionary<string, int> visibleRowIndexByTag = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<bool, Size> glyphSizeCache = new Dictionary<bool, Size>();

        private int updateDepth;
        private bool visibleRowsDirty;
        private bool allowUserSelection = true;
        private bool ensureActiveItemVisible = true;
        private string activeItemTag;
        private string lockedActiveItemTag;
        private bool isProgrammaticSelection;
        private bool suppressSelectionRestore;
        private ImageList glyphImages = CreateDefaultGlyphImages();

        private const int IndentPx = 16;
        private const int GlyphSize = 9;
        private int columnCount = 1;

        public event EventHandler<string> ActiveItemChanged;
        public event EventHandler<string> ItemDoubleClicked;
        public event EventHandler<string> ItemOpenedClosed;
        public event EventHandler<LvTreeCellClickedEventArgs> CellClicked;

        public ImageList GlyphImages
        {
            get => glyphImages;
            set
            {
                glyphImages = value ?? CreateDefaultGlyphImages();
                grid.Invalidate();
            }
        }

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

        public bool AllowUserSelection
        {
            get => allowUserSelection;
            set
            {
                allowUserSelection = value;
                if (!allowUserSelection)
                {
                    lockedActiveItemTag = ActiveItem?.Tag;
                    ClearGridSelectionState();
                }
                else
                {
                    SyncSelectionFromGrid();
                }
            }
        }

        public bool EnsureActiveItemVisible
        {
            get => ensureActiveItemVisible;
            set => ensureActiveItemVisible = value;
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

            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, grid, new object[] { true });

            grid.CellValueNeeded += Grid_CellValueNeeded;
            grid.CellPainting += Grid_CellPainting;
            grid.CellMouseDown += Grid_CellMouseDown;
            grid.CellMouseUp += Grid_CellMouseUp;
            grid.CellDoubleClick += Grid_CellDoubleClick;

            grid.MouseDown += (s, e) =>
            {
                if (!allowUserSelection)
                {
                    suppressSelectionRestore = true;
                }
            };
            grid.MouseUp += (s, e) =>
            {
                if (!allowUserSelection)
                {
                    RestoreLockedActiveItem();
                }
                suppressSelectionRestore = false;
            };

            grid.SelectionChanged += (s, e) =>
            {
                if (suppressSelectionRestore && !isProgrammaticSelection)
                {
                    suppressSelectionRestore = false;
                    return;
                }
                if (!allowUserSelection && !isProgrammaticSelection)
                {
                    RestoreLockedActiveItem();
                    return;
                }
                SyncSelectionFromGrid();
            };
            grid.CurrentCellChanged += (s, e) =>
            {
                if (suppressSelectionRestore && !isProgrammaticSelection)
                {
                    suppressSelectionRestore = false;
                    return;
                }
                if (isProgrammaticSelection || !allowUserSelection) return;
                int rowIndex = grid.CurrentCell?.RowIndex ?? -1;
                if (rowIndex >= 0 && rowIndex < visibleRows.Count && SetActiveItem(visibleRows[rowIndex]))
                    ActiveItemChanged?.Invoke(this, activeItemTag);
            };

            grid.HandleCreated += (s, e) =>
            {
                if (!allowUserSelection)
                    ClearGridSelectionState();
            };
            grid.VisibleChanged += (s, e) =>
            {
                if (!allowUserSelection && grid.Visible)
                    ClearGridSelectionState();
            };
            grid.Enter += (s, e) =>
            {
                if (!allowUserSelection)
                    ClearGridSelectionState();
            };
            grid.GotFocus += (s, e) =>
            {
                if (!allowUserSelection)
                    ClearGridSelectionState();
            };

            Controls.Add(grid);
        }

        public string[] ColumnHeaders
        {
            get => grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray();
            set
            {
                grid.Columns.Clear();
                columnCount = value.Length;
                foreach (var h in value)
                    grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
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
                return grid.Columns[columnIndex].DefaultCellStyle.Font ?? grid.Font;
            return grid.Font;
        }

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

        public LvTreeItem AddItemToEnd(string tag, string parentTag, string[] values, int glyphIndex = 0)
        {
            var node = AddItem(tag, parentTag, -1, values);
            node.GlyphIndex = glyphIndex;
            return node;
        }

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

        public void AddItemMultiple(string[] tags, string[] parentTags, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            AddItemMultiple(tags, parentTags, Enumerable.Repeat(0, tags.Length).ToArray(), values);
        }

        public void AddItemMultiple(string[] tags, string[] parentTags, int[] glyphIndices, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (parentTags == null) throw new ArgumentNullException(nameof(parentTags));
            if (glyphIndices == null) throw new ArgumentNullException(nameof(glyphIndices));
            if (values == null) throw new ArgumentNullException(nameof(values));

            int n = tags.Length;
            if (parentTags.Length != n || glyphIndices.Length != n || values.GetLength(0) != n)
                throw new ArgumentException($"Array length mismatch: tags={n}, parentTags={parentTags.Length}, glyphIndices={glyphIndices.Length}, values rows={values.GetLength(0)}.");

            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var rowValues = new string[values.GetLength(1)];
                    for (int c = 0; c < rowValues.Length; c++)
                        rowValues[c] = values[i, c];
                    string parentTag = string.IsNullOrEmpty(parentTags[i]) ? null : parentTags[i];
                    AddItemToEnd(tags[i], parentTag, rowValues, glyphIndices[i]);
                }
            }
            finally { EndUpdate(); }
        }

        public void AddItemMultiple(string[] tags, int[] itemIndents, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            AddItemMultiple(tags, itemIndents, Enumerable.Repeat(0, tags.Length).ToArray(), values);
        }

        public void AddItemMultiple(string[] tags, int[] itemIndents, int[] glyphIndices, string[,] values)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (itemIndents == null) throw new ArgumentNullException(nameof(itemIndents));
            if (glyphIndices == null) throw new ArgumentNullException(nameof(glyphIndices));
            if (values == null) throw new ArgumentNullException(nameof(values));

            int n = tags.Length;
            if (itemIndents.Length != n || glyphIndices.Length != n || values.GetLength(0) != n)
                throw new ArgumentException($"Array length mismatch: tags={n}, itemIndents={itemIndents.Length}, glyphIndices={glyphIndices.Length}, values rows={values.GetLength(0)}.");

            var ancestry = new List<LvTreeItem>();
            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int indent = itemIndents[i];
                    if (indent < 0) throw new ArgumentOutOfRangeException(nameof(itemIndents), "Item indentation cannot be negative.");
                    while (ancestry.Count > indent + 1)
                        ancestry.RemoveAt(ancestry.Count - 1);
                    while (ancestry.Count <= indent)
                        ancestry.Add(null);

                    LvTreeItem parent = indent > 0 ? ancestry[indent - 1] : null;
                    if (indent > 0 && parent == null)
                        throw new InvalidOperationException($"Item '{tags[i]}' requests indent {indent} but no parent exists at the previous level.");

                    var rowValues = new string[values.GetLength(1)];
                    for (int c = 0; c < rowValues.Length; c++)
                        rowValues[c] = values[i, c];

                    var node = AddItemToEnd(tags[i], parent?.Tag, rowValues, glyphIndices[i]);
                    ancestry[indent] = node;
                }
            }
            finally { EndUpdate(); }
        }

        public void RemoveItem(string tag)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            var siblings = node.Parent?.Children ?? roots;
            siblings.Remove(node);
            foreach (var child in node.Children.ToArray())
                child.Parent = node.Parent;
            RenumberLevels(node);
            allItems.Remove(tag);
            MarkDirtyOrRebuild();
        }

        public void RemoveItems(IEnumerable<string> tags)
        {
            BeginUpdate();
            try { foreach (var t in tags.ToArray()) RemoveItem(t); }
            finally { EndUpdate(); }
        }

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

        private static void InsertAt(List<LvTreeItem> siblings, LvTreeItem node, int position)
        {
            if (position < 0 || position >= siblings.Count)
                siblings.Add(node);
            else
                siblings.Insert(position, node);
        }

        public void ItemSetOpen(string tag, bool open)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            if (node.IsOpen == open) return;
            node.IsOpen = open;
            ItemOpenedClosed?.Invoke(this, tag);
            MarkDirtyOrRebuild();
        }

        public bool ItemExists(string tag) => allItems.ContainsKey(tag);
        public LvTreeItem GetItemReference(string tag) => allItems.TryGetValue(tag, out var node) ? node : null;

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

        public void SortItems(int columnIndex, bool ascending, Comparison<LvTreeItem> customComparer = null)
        {
            Comparison<LvTreeItem> cmp = customComparer ?? ((a, b) =>
            {
                var va = a.CellValues.Length > columnIndex ? a.CellValues[columnIndex] : null;
                var vb = b.CellValues.Length > columnIndex ? b.CellValues[columnIndex] : null;
                return ascending ? Comparer<object>.Default.Compare(va, vb) : -Comparer<object>.Default.Compare(va, vb);
            });

            void SortLevel(List<LvTreeItem> siblings)
            {
                siblings.Sort(cmp);
                foreach (var s in siblings) SortLevel(s.Children);
            }

            SortLevel(roots);
            MarkDirtyOrRebuild();
        }

        public void EditActiveItem()
        {
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            if (grid.CurrentCell != null)
                grid.BeginEdit(true);
        }

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
            if (changed) MarkDirtyOrRebuild();
        }

        public LvTreeItem ActiveItem => !string.IsNullOrEmpty(activeItemTag) && allItems.TryGetValue(activeItemTag, out var node) ? node : null;

        public LvTreeItem ActiveItem
        {
            get => ActiveItemCore;
            set
            {
                if (value == null) return;
                bool changed = SetActiveItem(value);
                bool suppressVisualNavigation = !allowUserSelection && !isProgrammaticSelection && (suppressSelectionRestore || !ensureActiveItemVisible);

                if (!suppressVisualNavigation && ensureActiveItemVisible)
                    EnsureVisible(value.Tag);

                int idx = GetVisibleRowIndex(value.Tag);
                int firstVisible = grid.FirstDisplayedScrollingRowIndex;
                int visibleCount = grid.DisplayedRowCount(false);
                bool isVisible = firstVisible >= 0 && idx >= firstVisible && idx < firstVisible + visibleCount;
                if (!suppressVisualNavigation && ensureActiveItemVisible && idx >= 0 && !isVisible)
                    grid.FirstDisplayedScrollingRowIndex = idx;

                isProgrammaticSelection = true;
                try
                {
                    if (allowUserSelection)
                    {
                        grid.ClearSelection();
                        if (idx >= 0 && grid.Columns.Count > 0 && (ensureActiveItemVisible || isVisible))
                            grid.CurrentCell = grid.Rows[idx].Cells[0];
                    }
                    else
                    {
                        lockedActiveItemTag = value.Tag;
                    }
                }
                finally { isProgrammaticSelection = false; }

                if (changed) ActiveItemChanged?.Invoke(this, activeItemTag);
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
                        if (allowUserSelection)
                        {
                            grid.ClearSelection();
                            grid.CurrentCell = null;
                        }
                        else
                        {
                            lockedActiveItemTag = null;
                        }
                    }
                    finally { isProgrammaticSelection = false; }
                    if (changed) ActiveItemChanged?.Invoke(this, null);
                }
                else if (allItems.TryGetValue(value, out var node))
                {
                    ActiveItem = node;
                }
            }
        }

        public int ActiveItemIndex => string.IsNullOrEmpty(activeItemTag) ? -1 : GetVisibleRowIndex(activeItemTag);

        private readonly HashSet<string> selectedTags = new HashSet<string>();
        public LvTreeItem[] SelectedItems => selectedTags.Where(allItems.ContainsKey).Select(t => allItems[t]).ToArray();
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
                ClearGridSelectionState();
                if (allowUserSelection && !string.IsNullOrEmpty(lockedActiveItemTag))
                {
                    int index = GetVisibleRowIndex(lockedActiveItemTag);
                    if (index >= 0 && grid.Columns.Count > 0)
                        grid.CurrentCell = grid.Rows[index].Cells[0];
                }
            }
            finally { isProgrammaticSelection = false; }
        }

        public void SetItemFillColor(string tag, Color color)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            node.RowBackColor = color;
            InvalidateRowIfVisible(node);
        }

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

        public void UpdateCellColorsMultiple(string[] tags, int columnStartIndex, Color[,] backColors, Color[,] foreColors)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            int n = tags.Length;
            bool hasBack = backColors != null && backColors.Length > 0;
            bool hasFore = foreColors != null && foreColors.Length > 0;
            if (hasBack && backColors.GetLength(0) != n) throw new ArgumentException($"Array length mismatch: tags={n}, backColors rows={backColors.GetLength(0)}.");
            if (hasFore && foreColors.GetLength(0) != n) throw new ArgumentException($"Array length mismatch: tags={n}, foreColors rows={foreColors.GetLength(0)}.");
            int backCols = hasBack ? backColors.GetLength(1) : 0;
            int foreCols = hasFore ? foreColors.GetLength(1) : 0;
            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    if (allItems.TryGetValue(tags[i], out var node))
                    {
                        if (hasBack)
                            for (int c = 0; c < backCols; c++)
                            {
                                int targetCol = columnStartIndex + c;
                                if (targetCol >= 0 && targetCol < node.CellBackColors.Length)
                                    node.CellBackColors[targetCol] = backColors[i, c];
                            }
                        if (hasFore)
                            for (int c = 0; c < foreCols; c++)
                            {
                                int targetCol = columnStartIndex + c;
                                if (targetCol >= 0 && targetCol < node.CellForeColors.Length)
                                    node.CellForeColors[targetCol] = foreColors[i, c];
                            }
                        InvalidateRowIfVisible(node);
                    }
                }
            }
            finally { EndUpdate(); }
        }

        public void UpdateCellBackColorsMultiple(string[] tags, int columnStartIndex, Color[,] backColors) => UpdateCellColorsMultiple(tags, columnStartIndex, backColors, null);
        public void UpdateCellForeColorsMultiple(string[] tags, int columnStartIndex, Color[,] foreColors) => UpdateCellColorsMultiple(tags, columnStartIndex, null, foreColors);

        private static int ColorToLabView(Color lvColor) => lvColor == Color.Transparent ? 0x01000000 : (int)((lvColor.R << 16) | (lvColor.G << 8) | lvColor.B);
        private static Color ColorFromLabView(int lvColor) => lvColor == 0x01000000 ? Color.Transparent : Color.FromArgb(255, (lvColor >> 16) & 0xFF, (lvColor >> 8) & 0xFF, lvColor & 0xFF);

        public void SetItemFillColor(string tag, int lvColor) => SetItemFillColor(tag, ColorFromLabView(lvColor));
        public void SetCellColor(string tag, int column, int lvBackColor) => SetCellColor(tag, column, ColorFromLabView(lvBackColor), null);
        public void SetCellColor(string tag, int column, int lvBackColor, int lvForeColor) => SetCellColor(tag, column, ColorFromLabView(lvBackColor), ColorFromLabView(lvForeColor));

        public void UpdateCellColorsMultiple(string[] tags, int columnStartIndex, int[,] lvBackColors, int[,] lvForeColors)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            bool hasBack = lvBackColors != null && lvBackColors.Length > 0;
            bool hasFore = lvForeColors != null && lvForeColors.Length > 0;
            Color[,] backColors = null;
            if (hasBack)
            {
                int rows = lvBackColors.GetLength(0);
                int cols = lvBackColors.GetLength(1);
                backColors = new Color[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        backColors[r, c] = ColorFromLabView(lvBackColors[r, c]);
            }
            Color[,] foreColors = null;
            if (hasFore)
            {
                int rows = lvForeColors.GetLength(0);
                int cols = lvForeColors.GetLength(1);
                foreColors = new Color[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        foreColors[r, c] = ColorFromLabView(lvForeColors[r, c]);
            }
            UpdateCellColorsMultiple(tags, columnStartIndex, backColors, foreColors);
        }

        public void UpdateCellBackColorsMultiple(string[] tags, int columnStartIndex, int[,] lvBackColors) => UpdateCellColorsMultiple(tags, columnStartIndex, lvBackColors, null);
        public void UpdateCellForeColorsMultiple(string[] tags, int columnStartIndex, int[,] lvForeColors) => UpdateCellColorsMultiple(tags, columnStartIndex, null, lvForeColors);

        public void SetCellsColor(string[] tags, int[] columns, Color backColor, Color? foreColor = null)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (columns == null) throw new ArgumentNullException(nameof(columns));
            int n = tags.Length;
            if (columns.Length != n) throw new ArgumentException($"Array length mismatch: tags={n}, columns={columns.Length}.");
            BeginUpdate();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    if (allItems.TryGetValue(tags[i], out var node))
                    {
                        int col = columns[i];
                        if (col >= 0 && col < node.CellBackColors.Length)
                        {
                            node.CellBackColors[col] = backColor;
                            if (foreColor.HasValue) node.CellForeColors[col] = foreColor.Value;
                        }
                        InvalidateRowIfVisible(node);
                    }
                }
            }
            finally { EndUpdate(); }
        }

        public void SetCellsColor(string[] tags, int[] columns, int lvBackColor) => SetCellsColor(tags, columns, ColorFromLabView(lvBackColor), null);
        public void SetCellsColor(string[] tags, int[] columns, int lvBackColor, int lvForeColor) => SetCellsColor(tags, columns, ColorFromLabView(lvBackColor), ColorFromLabView(lvForeColor));

        public void ExpandAll()
        {
            BeginUpdate();
            try { foreach (var node in allItems.Values) node.IsOpen = true; }
            finally { EndUpdate(); }
        }

        public void CollapseAll()
        {
            BeginUpdate();
            try { foreach (var node in allItems.Values) node.IsOpen = false; }
            finally { EndUpdate(); }
        }

        public void ExpandToItem(string tag)
        {
            if (!allItems.TryGetValue(tag, out var node)) return;
            var parent = node.Parent;
            while (parent != null)
            {
                if (!parent.IsOpen)
                {
                    parent.IsOpen = true;
                }
                parent = parent.Parent;
            }
            MarkDirtyOrRebuild();
        }

        public void AutoFitColumns()
        {
            using (var g = grid.CreateGraphics())
            {
                for (int col = 0; col < grid.Columns.Count; col++)
                {
                    int maxWidth = 0;
                    var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
                    var headerText = grid.Columns[col].HeaderText;
                    var headerSize = TextRenderer.MeasureText(g, headerText, headerFont);
                    maxWidth = Math.Max(maxWidth, headerSize.Width + 10);
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
                            if (HasGlyph(node)) width += glyphImages.ImageSize.Width + 4;
                        }
                        if (width > maxWidth) maxWidth = width;
                    }
                    if (maxWidth > 0) grid.Columns[col].Width = Math.Max(maxWidth, 40);
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
            if (updateDepth > 0) return;
            int idx = GetVisibleRowIndex(node.Tag);
            if (idx >= 0) grid.InvalidateRow(idx);
        }

        private void ClearGridSelectionState()
        {
            if (grid.IsDisposed) return;
            if (grid.Rows.Count > 0)
            {
                foreach (DataGridViewRow row in grid.Rows)
                    row.Selected = false;
            }
            grid.ClearSelection();
            if (grid.Columns.Count > 0)
                grid.CurrentCell = null;
            foreach (var node in allItems.Values)
                node.Selected = false;
            selectedTags.Clear();
        }

        private void MarkDirtyOrRebuild()
        {
            if (updateDepth > 0) { visibleRowsDirty = true; return; }
            RebuildVisibleRows();
        }

        private void RebuildVisibleRows()
        {
            visibleRowsDirty = false;
            visibleRows = new List<LvTreeItem>(allItems.Count);
            visibleRowIndexByTag.Clear();
            void Walk(List<LvTreeItem> siblings)
            {
                foreach (var s in siblings)
                {
                    visibleRows.Add(s);
                    visibleRowIndexByTag[s.Tag] = visibleRows.Count - 1;
                    if (s.IsOpen && s.HasChildren)
                        Walk(s.Children);
                }
            }
            Walk(roots);
            grid.RowCount = visibleRows.Count;
            ClearGridSelectionState();
            grid.Invalidate();
        }

        private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count) return;
            var node = visibleRows[e.RowIndex];
            e.Value = (e.ColumnIndex >= 0 && e.ColumnIndex < node.CellValues.Length) ? node.CellValues[e.ColumnIndex] : null;
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex < 0)
                return;
            var node = visibleRows[e.RowIndex];
            Color back = (node.CellBackColors[e.ColumnIndex] != Color.Empty) ? node.CellBackColors[e.ColumnIndex] : (node.RowBackColor != Color.Empty ? node.RowBackColor : e.CellStyle.BackColor);
            Color fore = (node.CellForeColors[e.ColumnIndex] != Color.Empty) ? node.CellForeColors[e.ColumnIndex] : e.CellStyle.ForeColor;
            if (node.Selected || node.Active) { back = e.CellStyle.SelectionBackColor; fore = e.CellStyle.SelectionForeColor; }
            using (var backBrush = new SolidBrush(back)) e.Graphics.FillRectangle(backBrush, e.CellBounds);
            int textLeft = e.CellBounds.Left + 4;
            if (e.ColumnIndex == 0)
            {
                int indent = node.Level * IndentPx;
                textLeft += indent;
                if (node.HasChildren)
                {
                    Size glyphSize = GetGlyphSize(e.Graphics, node.IsOpen);
                    int gx = e.CellBounds.Left + indent;
                    int gy = e.CellBounds.Top + (e.CellBounds.Height - glyphSize.Height) / 2;
                    DrawExpanderGlyph(e.Graphics, gx, gy, glyphSize, node.IsOpen);
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
            TextRenderer.DrawText(e.Graphics, text, e.CellStyle.Font, new Rectangle(textLeft, e.CellBounds.Top, e.CellBounds.Right - textLeft, e.CellBounds.Height), fore, flags);
            e.Handled = true;
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex != 0) return;
            var node = visibleRows[e.RowIndex];
            if (!node.HasChildren) return;
            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Size glyphSize = GetGlyphSize(grid.CreateGraphics(), node.IsOpen);
            var glyphRect = new Rectangle(cellRect.Left + node.Level * IndentPx, cellRect.Top + (cellRect.Height - glyphSize.Height) / 2, glyphSize.Width, glyphSize.Height);
            glyphRect.Inflate(3, 3);
            Point gridPoint = new Point(e.X, e.Y);
            Point cellPoint = new Point(cellRect.Left + e.X, cellRect.Top + e.Y);
            if (glyphRect.Contains(gridPoint) || glyphRect.Contains(cellPoint))
                ItemSetOpen(node.Tag, !node.IsOpen);
        }

        private void Grid_CellMouseUp(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.RowIndex < 0 || e.RowIndex >= visibleRows.Count || e.ColumnIndex < 0) return;
            var node = visibleRows[e.RowIndex];
            CellClicked?.Invoke(this, new LvTreeCellClickedEventArgs(node.Tag, e.ColumnIndex, IsGlyphHit(node, e), IsExpanderHit(node, e)));
        }

        private bool IsExpanderHit(LvTreeItem node, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != 0 || !node.HasChildren) return false;
            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Size expanderSize = GetGlyphSize(grid.CreateGraphics(), node.IsOpen);
            int left = cellRect.Left + node.Level * IndentPx;
            var expanderRect = new Rectangle(left, cellRect.Top + (cellRect.Height - expanderSize.Height) / 2, expanderSize.Width, expanderSize.Height);
            expanderRect.Inflate(3, 3);
            return IsMousePointInRectangle(e, cellRect, expanderRect);
        }

        private bool IsGlyphHit(LvTreeItem node, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex != 0 || !HasGlyph(node)) return false;
            var cellRect = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Size expanderSize = GetGlyphSize(grid.CreateGraphics(), node.IsOpen);
            int left = cellRect.Left + node.Level * IndentPx + expanderSize.Width + 4;
            Size itemGlyphSize = glyphImages.ImageSize;
            var itemGlyphRect = new Rectangle(left, cellRect.Top + (cellRect.Height - itemGlyphSize.Height) / 2, itemGlyphSize.Width, itemGlyphSize.Height);
            return IsMousePointInRectangle(e, cellRect, itemGlyphRect);
        }

        private static bool IsMousePointInRectangle(DataGridViewCellMouseEventArgs e, Rectangle cellRect, Rectangle targetRect)
        {
            Point gridPoint = new Point(e.X, e.Y);
            Point cellPoint = new Point(cellRect.Left + e.X, cellRect.Top + e.Y);
            return targetRect.Contains(gridPoint) || targetRect.Contains(cellPoint);
        }

        private static void DrawExpanderGlyph(Graphics g, int x, int y, Size size, bool isOpen)
        {
            if (Application.RenderWithVisualStyles)
            {
                var element = isOpen ? VisualStyleElement.TreeView.Glyph.Opened : VisualStyleElement.TreeView.Glyph.Closed;
                if (VisualStyleRenderer.IsElementDefined(element))
                {
                    var renderer = new VisualStyleRenderer(element);
                    renderer.DrawBackground(g, new Rectangle(x, y, size.Width, size.Height));
                    return;
                }
            }
            using var pen = new Pen(SystemColors.ControlText);
            g.DrawRectangle(pen, x + 1, y + 1, size.Width - 2, size.Height - 2);
            g.DrawLine(pen, x + 2, y + size.Height / 2, x + size.Width - 3, y + size.Height / 2);
            if (!isOpen) g.DrawLine(pen, x + size.Width / 2, y + 2, x + size.Width / 2, y + size.Height - 3);
        }

        private Size GetGlyphSize(Graphics g, bool isOpen)
        {
            if (glyphSizeCache.TryGetValue(isOpen, out var cached)) return cached;
            Size size;
            if (Application.RenderWithVisualStyles)
            {
                var element = isOpen ? VisualStyleElement.TreeView.Glyph.Opened : VisualStyleElement.TreeView.Glyph.Closed;
                if (VisualStyleRenderer.IsElementDefined(element))
                {
                    var renderer = new VisualStyleRenderer(element);
                    size = renderer.GetPartSize(g, ThemeSizeType.True);
                }
                else size = new Size(GlyphSize, GlyphSize);
            }
            else size = new Size(GlyphSize, GlyphSize);
            glyphSizeCache[isOpen] = size;
            return size;
        }

        private int GetVisibleRowIndex(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return -1;
            return visibleRowIndexByTag.TryGetValue(tag, out var index) ? index : -1;
        }

        private bool HasGlyph(LvTreeItem node) => node.GlyphIndex > 0 && node.GlyphIndex < glyphImages.Images.Count;

        private static TextFormatFlags GetTextFormatFlags(DataGridViewContentAlignment alignment) => alignment switch
        {
            DataGridViewContentAlignment.MiddleRight => TextFormatFlags.Right,
            DataGridViewContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left
        };

        private static Bitmap DrawCheckBoxGlyph(bool isChecked)
        {
            var bitmap = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = new Rectangle(2, 2, 11, 11);
            using var fill = new SolidBrush(isChecked ? Color.FromArgb(0, 120, 215) : Color.White);
            graphics.FillRectangle(fill, bounds);
            using var border = new Pen(Color.FromArgb(90, 90, 90));
            graphics.DrawRectangle(border, bounds);
            if (isChecked)
            {
                using var check = new Pen(Color.White, 2f);
                check.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                check.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                graphics.DrawLines(check, new[] { new Point(4, 7), new Point(6, 10), new Point(11, 4) });
            }
            return bitmap;
        }

        private static Bitmap DrawPowerGlyph(Color color)
        {
            var bitmap = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 2, 2, 12, 12);
            using var innerBrush = new SolidBrush(Color.White);
            graphics.FillEllipse(innerBrush, 5, 5, 6, 6);
            return bitmap;
        }
    }
}
