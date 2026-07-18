using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Bot.ChromeNs;
using Bot.Options;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Bot.Knowledge
{
    public class KnowledgeManagerControl : UserControl
    {
        private List<KnowledgeBaseEntry> _all = new List<KnowledgeBaseEntry>(); private ObservableCollection<KnowledgeBaseEntry> _view = new ObservableCollection<KnowledgeBaseEntry>();
        private TextBox _search; private ComboBox _cat; private TextBlock _count; private DataGrid _grid;
        public KnowledgeManagerControl(){Build();RefreshData();Loaded+=(s,e)=>KnowledgeLearningService.KnowledgeBaseChanged+=OnKnowledgeBaseChanged;Unloaded+=(s,e)=>KnowledgeLearningService.KnowledgeBaseChanged-=OnKnowledgeBaseChanged;}
        private void Build(){var root=new DockPanel{Margin=new Thickness(10)};Content=root;var top=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);_search=new TextBox{Width=240,Height=28,Text="",ToolTip="请输入问题、答案、关键词..."};_search.TextChanged+=(s,e)=>ApplyFilter();top.Children.Add(_search);_cat=new ComboBox{Width=180,Height=28,Margin=new Thickness(8,0,8,0)};_cat.SelectionChanged+=(s,e)=>ApplyFilter();top.Children.Add(_cat);AddBtn(top,"搜索",70,(s,e)=>ApplyFilter());AddBtn(top,"清空",70,(s,e)=>{_search.Text="";_cat.SelectedIndex=0;ApplyFilter();});AddBtn(top,"新增问答",90,(s,e)=>AddNew());AddBtn(top,"导入JSON",90,(s,e)=>ImportJson());AddBtn(top,"导出JSON",90,(s,e)=>ExportJson());_count=new TextBlock{Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(_count,Dock.Bottom);root.Children.Add(_count);_grid=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,ItemsSource=_view,IsReadOnly=true};_grid.MouseDoubleClick+=(s,e)=>EditSelected();root.Children.Add(_grid);_grid.Columns.Add(new DataGridCheckBoxColumn{Header="启用",Binding=new Binding("Enabled"),Width=55});_grid.Columns.Add(new DataGridTextColumn{Header="分类",Binding=new Binding("Category"),Width=130});_grid.Columns.Add(new DataGridTextColumn{Header="问题",Binding=new Binding("Title"),Width=220});_grid.Columns.Add(new DataGridTextColumn{Header="答案",Binding=new Binding("Answer"),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});_grid.Columns.Add(new DataGridTextColumn{Header="关键词",Binding=new Binding("Keywords"),Width=160});_grid.Columns.Add(new DataGridTextColumn{Header="更新时间",Binding=new Binding("UpdatedAt"),Width=140});var ops=new DataGridTemplateColumn{Header="操作",Width=120,CellTemplate=OpTemplate()};_grid.Columns.Add(ops);}
        private void AddBtn(Panel p,string t,double w,RoutedEventHandler h){var b=new Button{Content=t,Width=w,Height=28,Margin=new Thickness(0,0,8,0)};b.Click+=h;p.Children.Add(b);}        
        private DataTemplate OpTemplate(){var dt=new DataTemplate();var sp=new FrameworkElementFactory(typeof(StackPanel));sp.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);var e=new FrameworkElementFactory(typeof(Button));e.SetValue(Button.ContentProperty,"编辑");e.SetValue(Button.MarginProperty,new Thickness(0,0,4,0));e.AddHandler(Button.ClickEvent,new RoutedEventHandler((s,a)=>{_grid.SelectedItem=((FrameworkElement)s).DataContext;EditSelected();}));sp.AppendChild(e);var d=new FrameworkElementFactory(typeof(Button));d.SetValue(Button.ContentProperty,"删除");d.AddHandler(Button.ClickEvent,new RoutedEventHandler((s,a)=>{_grid.SelectedItem=((FrameworkElement)s).DataContext;DeleteSelected();}));sp.AppendChild(d);dt.VisualTree=sp;return dt;}
        public void RefreshData(){_all=BotFeatureStore.GetKnowledgeBase();RefreshCategories();ApplyFilter();}
        private void RefreshCategories(){var old=_cat.SelectedItem as string;_cat.Items.Clear();_cat.Items.Add("全部分类");foreach(var c in _all.Select(x=>x.Category??"").Where(x=>x.Length>0).Distinct().OrderBy(x=>x))_cat.Items.Add(c);_cat.SelectedItem=!string.IsNullOrWhiteSpace(old)&&_cat.Items.Contains(old)?old:"全部分类";}
        private void ApplyFilter(){if(_view==null)return;var q=(_search==null?"":_search.Text).Trim();var c=_cat==null?"全部分类":(_cat.SelectedItem as string??"全部分类");var list=_all.Where(x=>Match(x,q,c)).ToList();_view.Clear();foreach(var x in list)_view.Add(x);if(_count!=null)_count.Text="共 "+_all.Count+" 条知识，当前显示 "+_view.Count+" 条";}
        private bool Match(KnowledgeBaseEntry x,string q,string c){if(c!="全部分类"&&(x.Category??"")!=c)return false;if(string.IsNullOrWhiteSpace(q))return true;return ((x.Category??"")+" "+(x.Title??"")+" "+(x.Answer??"")+" "+(x.Keywords??"")).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0;}
        private void AddNew(){var it=new KnowledgeBaseEntry{Enabled=true,Category="通用"};OpenEditor(it,true);} private void EditSelected(){var it=_grid.SelectedItem as KnowledgeBaseEntry;if(it!=null)OpenEditor(it,false);}        
        private void OpenEditor(KnowledgeBaseEntry it,bool add){var wnd=new KnowledgeEditWindow(it,_all.Select(x=>x.Category));wnd.Owner=Window.GetWindow(this);if(wnd.ShowDialog()==true){if(add)_all.Add(it);BotFeatureStore.SaveKnowledgeBase(_all);RefreshCategories();ApplyFilter();}}
        private void DeleteSelected(){var it=_grid.SelectedItem as KnowledgeBaseEntry;if(it==null)return;if(MessageBox.Show("确定删除这条知识吗？","确认删除",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;_all.Remove(it);BotFeatureStore.SaveKnowledgeBase(_all);RefreshCategories();ApplyFilter();}
        private void ImportJson(){try{var dlg=new OpenFileDialog{Title="导入知识库",Filter="JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"};if(dlg.ShowDialog()!=true)return;var list=JsonConvert.DeserializeObject<List<KnowledgeBaseEntry>>(File.ReadAllText(dlg.FileName,System.Text.Encoding.UTF8));if(list==null)throw new Exception("JSON中没有知识库数据");var overwrite=MessageBox.Show("是否覆盖全部知识？\n\n选择“否”将追加导入并自动按问题去重。","导入方式",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);if(overwrite==MessageBoxResult.Cancel)return;if(overwrite==MessageBoxResult.Yes)_all=list;else{var seen=new HashSet<string>(_all.Select(x=>KnowledgeAiService.NormalizeQuestion(x.Title)));foreach(var it in list){var key=KnowledgeAiService.NormalizeQuestion(it.Title);if(!seen.Contains(key)){_all.Add(it);seen.Add(key);}}}BotFeatureStore.SaveKnowledgeBase(_all);RefreshCategories();ApplyFilter();}catch(Exception ex){MessageBox.Show("导入失败："+ex.Message);}}
        private void ExportJson(){try{var dlg=new SaveFileDialog{Title="导出知识库",FileName="qianniu-knowledge-base.json",Filter="JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"};if(dlg.ShowDialog()!=true)return;File.WriteAllText(dlg.FileName,JsonConvert.SerializeObject(_all,Formatting.Indented),System.Text.Encoding.UTF8);}catch(Exception ex){MessageBox.Show("导出失败："+ex.Message);}}
        private void OnKnowledgeBaseChanged(object sender,EventArgs e){if(Dispatcher.CheckAccess())RefreshData();else Dispatcher.BeginInvoke(new Action(RefreshData));}
    }
}
