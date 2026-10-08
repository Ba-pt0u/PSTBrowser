using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App
{
    public enum NodeKind { All, Mailbox, Folder }

    public sealed class TreeNode : INotifyPropertyChanged
    {
        private string _name;
        private long _count;
        private bool _isExpanded;
        private bool _isSelected;

        public string Key { get; set; }
        public NodeKind Kind { get; set; }
        public long? MailboxId { get; set; }
        public string FolderPath { get; set; }
        public TreeNode Parent { get; set; }
        public ObservableCollection<TreeNode> Children { get; } = new ObservableCollection<TreeNode>();

        public string Name { get => _name; set { if (_name != value) { _name = value; OnChanged(); } } }
        public long Count { get => _count; set { if (_count != value) { _count = value; OnChanged(); OnChanged(nameof(CountText)); } } }
        public string CountText => _count > 0 ? _count.ToString("N0") : "";
        public string Icon => Kind == NodeKind.All ? "🗂" : Kind == NodeKind.Mailbox ? "👤" : "📁";
        public FontWeight Weight => Kind == NodeKind.Folder ? FontWeights.Normal : FontWeights.SemiBold;
        public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) { _isExpanded = value; OnChanged(); } } }
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnChanged(); } } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public IEnumerable<TreeNode> Descendants()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }

        /// <summary>Updates this collection to match <paramref name="desired"/> (by Key), preserving existing nodes and their UI state.</summary>
        public static void Merge(ObservableCollection<TreeNode> current, IList<TreeNode> desired, TreeNode parent)
        {
            var desiredKeys = new HashSet<string>(desired.Select(d => d.Key));
            for (int i = current.Count - 1; i >= 0; i--)
                if (!desiredKeys.Contains(current[i].Key)) current.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                var d = desired[i];
                var existing = current.FirstOrDefault(c => c.Key == d.Key);
                if (existing == null)
                {
                    d.Parent = parent;
                    current.Insert(Math.Min(i, current.Count), d);
                    FixParents(d);
                    continue;
                }
                existing.Name = d.Name;
                existing.Count = d.Count;
                int idx = current.IndexOf(existing);
                if (idx != i && i < current.Count) current.Move(idx, i);
                Merge(existing.Children, d.Children.ToList(), existing);
            }
        }

        private static void FixParents(TreeNode n)
        {
            foreach (var c in n.Children) { c.Parent = n; FixParents(c); }
        }
    }

    /// <summary>Row of the message list (wraps the engine's MessageRow with display helpers).</summary>
    public sealed class RowItem
    {
        public MessageRow Row { get; }
        public RowItem(MessageRow row) { Row = row; }

        public long Id => Row.Id;
        public DateTime? Date => Row.Date;
        private const string DateFormat = "dd/MM/yy HH:mm";
        public string DateText => Row.Date?.ToString(DateFormat) ?? "";
        public string SentText => Row.SentDate?.ToString(DateFormat) ?? "";
        public string From => Row.From;
        public string SenderEmail => Row.SenderEmail;
        public string To => Row.To;
        public string Cc => Row.Cc;
        public string Bcc => Row.Bcc;
        public string Subject => Row.Subject;
        public string SubjectText
        {
            get
            {
                var s = string.IsNullOrWhiteSpace(Row.Subject) ? "(sans objet)" : Row.Subject;
                if (Row.Kind != "Message") s = $"[{Row.Kind}] {s}";
                if (Row.Error != null) s = "⚠ " + s;
                return s;
            }
        }
        public string Snippet => Row.Snippet;
        public Visibility SnippetVisibility => string.IsNullOrEmpty(Row.Snippet) ? Visibility.Collapsed : Visibility.Visible;
        public string Mailbox => Row.Mailbox;
        public string FolderName
        {
            get
            {
                var p = Row.FolderPath ?? "";
                int i = p.LastIndexOf('/');
                return i >= 0 ? p.Substring(i + 1) : p;
            }
        }
        public string FolderPathText => Row.FolderPath;
        public string SourceName => Row.SourceName;
        public string SizeText => Format.Size(Row.Size);
        public long Size => Row.Size;
        public bool HasAttachments => Row.HasAttachments;
        public string AttachmentMark => Row.HasAttachments ? "📎" : "";
        public string AttachmentCountText => Row.AttachmentCount > 0 ? Row.AttachmentCount.ToString() : "";
        public string AttachmentNames => Row.AttachmentNames;
        public string KindText => Row.Kind;
        public string ImportanceText => Row.Importance == 2 ? "Haute" : Row.Importance == 0 ? "Basse" : "";
        public bool IsDuplicate => Row.IsDuplicate;
        public string DuplicateText => Row.IsDuplicate ? "oui" : "";
        public string MessageId => Row.MessageId;
        public string ConversationTopic => Row.ConversationTopic;
        public string ReadText => Row.IsRead == true ? "Lu" : Row.IsRead == false ? "Non lu" : "";
        public string FlagText => Row.FlagStatus == 2 ? "🚩 À suivre" : Row.FlagStatus == 1 ? "✔ Terminé" : "";
        public bool IsUnread => Row.IsRead == false;
        public string TooltipText
        {
            get
            {
                var t = $"{Row.Mailbox} › {Row.FolderPath?.Replace("/", " › ")}";
                if (Row.HasAttachments && !string.IsNullOrEmpty(Row.AttachmentNames)) t += "\nPièces jointes : " + Row.AttachmentNames;
                if (Row.IsDuplicate) t += "\n(doublon d'un autre message de la même boîte)";
                if (Row.Error != null) t += "\nErreur de lecture : " + Row.Error;
                if (!string.IsNullOrEmpty(Row.Snippet)) t += "\n\n" + Row.Snippet;
                return t;
            }
        }
    }
}
