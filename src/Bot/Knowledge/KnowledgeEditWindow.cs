using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Bot.Options;

namespace Bot.Knowledge
{
    public class KnowledgeEditWindow : Window
    {
        private ComboBox _cat; private TextBox _title; private TextBox _answer; private TextBox _keywords; private CheckBox _enabled;
        public KnowledgeBaseEntry Entry { get; private set; }
        public KnowledgeEditWindow(KnowledgeBaseEntry entry, IEnumerable<string> categories)
        {
            Entry = entry; Title = "问答编辑"; Width = 620; Height = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(14) }; Content = sp;
            _cat = new ComboBox { IsEditable = true, Height = 26, ItemsSource = (categories ?? new string[0]).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToList(), Text = entry.Category ?? "通用" }; Add(sp,"分类",_cat);
            _title = new TextBox { Text = entry.Title ?? string.Empty, Height = 28 }; Add(sp,"问题",_title);
            _answer = new TextBox { Text = entry.Answer ?? string.Empty, Height = 160, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Add(sp,"答案",_answer);
            _keywords = new TextBox { Text = entry.Keywords ?? string.Empty, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }; Add(sp,"关键词",_keywords);
            _enabled = new CheckBox { Content = "启用", IsChecked = entry.Enabled, Margin = new Thickness(80,8,0,8) }; sp.Children.Add(_enabled);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; sp.Children.Add(row);
            var save = new Button { Content = "保存", Width = 80, Height = 30, Margin = new Thickness(0,0,8,0) }; var cancel = new Button { Content = "取消", Width = 80, Height = 30 };
            row.Children.Add(save); row.Children.Add(cancel); save.Click += (s,e)=>Save(); cancel.Click += (s,e)=>DialogResult=false;
        }
        private void Add(StackPanel sp,string label,Control c){ var row=new DockPanel{Margin=new Thickness(0,0,0,10)}; sp.Children.Add(row); row.Children.Add(new TextBlock{Text=label,Width=80,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,4,0,0)}); row.Children.Add(c); }
        private void Save(){ if(string.IsNullOrWhiteSpace(_title.Text)||string.IsNullOrWhiteSpace(_answer.Text)){MessageBox.Show("问题和答案不能为空。","提示");return;} Entry.Category=string.IsNullOrWhiteSpace(_cat.Text)?"通用":_cat.Text.Trim(); Entry.Title=_title.Text.Trim(); Entry.Answer=_answer.Text.Trim(); Entry.Keywords=_keywords.Text.Trim(); Entry.Enabled=_enabled.IsChecked??true; Entry.UpdatedAt=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); if(string.IsNullOrWhiteSpace(Entry.Id))Entry.Id=Guid.NewGuid().ToString("N"); if(string.IsNullOrWhiteSpace(Entry.CreatedAt))Entry.CreatedAt=Entry.UpdatedAt; DialogResult=true; }
    }
}
