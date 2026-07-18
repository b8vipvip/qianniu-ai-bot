using System;
using System.Collections.ObjectModel;
using System.Threading;
using Bot.Options;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.Knowledge
{
    public class KnowledgeImportControl : UserControl
    {
        private TextBox _text; private TextBox _timeout; private TextBlock _summary; private TextBlock _status; private Button _start; private Button _cancel; private ListBox _media; private ClipboardKnowledgeData _data; private readonly Action _showManager; private CancellationTokenSource _cts; private SmartImportCancelSource _cancelSource;
        public KnowledgeImportControl(Action showManager)
        {
            _showManager = showManager; _data = new ClipboardKnowledgeData(); Build(); PreviewKeyDown += OnKeyDown;
        }
        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
            var tip = new TextBlock { Text = "将商品详情、店铺资料、售后规则、产品介绍等内容直接复制后粘贴到这里，AI会自动整理成客服问答知识库。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0,0,0,8) };
            DockPanel.SetDock(tip, Dock.Top); root.Children.Add(tip);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) }; DockPanel.SetDock(btns, Dock.Top); root.Children.Add(btns);
            var read = Btn("从剪贴板读取",120); read.Click += (s,e)=>ReadClipboard(); btns.Children.Add(read);
            var clear = Btn("清空",70); clear.Click += (s,e)=>Clear(); btns.Children.Add(clear);
            btns.Children.Add(new TextBlock{Text="AI分析超时：",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)}); _timeout=new TextBox{Width=58,Height=28,Text=BotFeatureStore.GetSmartImportTimeoutSeconds().ToString()}; btns.Children.Add(_timeout); btns.Children.Add(new TextBlock{Text=" 秒",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0)}); _start = Btn("开始智能导入",130); _start.Click += async (s,e)=>await StartImport(); btns.Children.Add(_start); _cancel=Btn("取消",70); _cancel.IsEnabled=false; _cancel.Click+=(s,e)=>{_cancelSource=SmartImportCancelSource.UserCancel; if(_cts!=null)_cts.Cancel();}; btns.Children.Add(_cancel);
            _summary = new TextBlock { Text = "已识别：文字：0 字，图片：0 张，视频：0 个", Margin = new Thickness(0,0,0,8), FontWeight = FontWeights.Bold }; DockPanel.SetDock(_summary, Dock.Top); root.Children.Add(_summary);
            _status = new TextBlock { Foreground = Brushes.RoyalBlue, Margin = new Thickness(0,0,0,8) }; DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(grid);
            _text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Microsoft YaHei"), AllowDrop = true }; Grid.SetColumn(_text,0); grid.Children.Add(_text);
            var side = new DockPanel { Margin = new Thickness(10,0,0,0) }; Grid.SetColumn(side,1); grid.Children.Add(side);
            side.Children.Add(new TextBlock { Text = "媒体列表（选中后可删除）", FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,6) });
            var del = Btn("删除选中媒体",120); del.Click += (s,e)=>DeleteSelected(); DockPanel.SetDock(del, Dock.Bottom); side.Children.Add(del);
            _media = new ListBox { Margin = new Thickness(0,26,0,8) }; side.Children.Add(_media);
        }
        public void CancelForWindowClose(){_cancelSource=SmartImportCancelSource.WindowClosed;if(_cts!=null)_cts.Cancel();}
        private Button Btn(string t,double w){return new Button{Content=t,Width=w,Height=30,Margin=new Thickness(0,0,8,0)};}
        private void OnKeyDown(object s, KeyEventArgs e){ if((Keyboard.Modifiers&ModifierKeys.Control)==ModifierKeys.Control && e.Key==Key.V){ ReadClipboard(); e.Handled=true; }}
        private void ReadClipboard(){ try{ _status.Text="正在读取剪贴板..."; _data=KnowledgeClipboardParser.ReadClipboard(); _text.Text=_data.Text; Refresh(); _status.Text="剪贴板读取完成。";}catch(Exception ex){MessageBox.Show(ex.Message,"读取剪贴板失败",MessageBoxButton.OK,MessageBoxImage.Warning);} }
        private void Clear(){ _data=new ClipboardKnowledgeData(); _text.Clear(); Refresh(); _status.Text="已清空。"; }
        private void Refresh(){ if(_data==null)_data=new ClipboardKnowledgeData(); _data.Text=_text.Text; _summary.Text=string.Format("已识别：文字：{0:N0} 字，图片：{1} 张，视频：{2} 个",(_data.Text??"").Length,_data.Images.Count,_data.Videos.Count); _media.ItemsSource=null; var list=new ObservableCollection<KnowledgeMediaItem>(); foreach(var x in _data.Images)list.Add(x); foreach(var x in _data.Videos)list.Add(x); _media.ItemsSource=list; }
        private void DeleteSelected(){ var it=_media.SelectedItem as KnowledgeMediaItem; if(it==null)return; _data.Images.Remove(it); _data.Videos.Remove(it); Refresh(); }
        private bool ConfirmSkipVideo(){ var wnd=new Window{Title="检测到视频",Width=430,Height=210,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Owner=Window.GetWindow(this)}; var sp=new StackPanel{Margin=new Thickness(18)}; wnd.Content=sp; sp.Children.Add(new TextBlock{Text="检测到 "+_data.Videos.Count+" 个视频文件。\n\n当前配置的 AI 接口暂不支持直接视频理解。\n\n你可以跳过视频，仅分析文字和图片后继续导入；\n也可以取消本次导入。",TextWrapping=TextWrapping.Wrap}); var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)}; sp.Children.Add(row); var ok=Btn("跳过视频并继续",140); var cancel=Btn("取消本次导入",120); row.Children.Add(ok); row.Children.Add(cancel); ok.Click+=(s,e)=>{wnd.DialogResult=true;}; cancel.Click+=(s,e)=>{wnd.DialogResult=false;}; return wnd.ShowDialog()==true; }
        private async System.Threading.Tasks.Task StartImport(){ Refresh(); int timeout; if(!int.TryParse(_timeout.Text,out timeout))timeout=600; timeout=KnowledgeAiService.ClampTimeout(timeout); _timeout.Text=timeout.ToString(); BotFeatureStore.SaveSmartImportTimeoutSeconds(timeout); if(!_data.HasAnalyzableContent){MessageBox.Show("没有检测到可导入的文字、图片或媒体内容。","提示",MessageBoxButton.OK,MessageBoxImage.Information);return;} var svc=new KnowledgeAiService(); if(_data.Videos.Count>0&&!svc.SupportsDirectVideo&&!ConfirmSkipVideo())return; if(_cts!=null){_cancelSource=SmartImportCancelSource.ReplacedByNewTask;_cts.Cancel();} _cts=new CancellationTokenSource(); _cancelSource=SmartImportCancelSource.None; _start.IsEnabled=false; _cancel.IsEnabled=true; try{ var r=await svc.ImportAsync(_data,timeout,_cts.Token,()=>_cancelSource,m=>Dispatcher.Invoke(()=>_status.Text=m)); _status.Text="导入完成。"; var msg=string.Format("知识库导入成功\n\n本次分析：\n文字：{0:N0} 字\n图片：{1} 张\n跳过视频：{2} 个\n\nAI生成问答：{3} 条\n成功新增：{4} 条\n重复跳过：{5} 条\n新增分类：{6} 个",r.TextChars,r.ImageCount,r.VideoSkipped,r.AiGenerated,r.Added,r.DuplicateSkipped,r.NewCategoryCount); if(r.UnsupportedImageSkipped>0) msg += "\n\n有 "+r.UnsupportedImageSkipped+" 张图片因当前 AI 接口不支持图片理解而未参与分析。"; var res=MessageBox.Show(msg+"\n\n点击“是”查看问答，点击“否”关闭。","知识库导入成功",MessageBoxButton.YesNo,MessageBoxImage.Information); if(res==MessageBoxResult.Yes && _showManager!=null)_showManager(); }catch(SmartImportException ex){ if(ex.Source!=SmartImportCancelSource.WindowClosed) MessageBox.Show(ex.Message,"智能导入停止",MessageBoxButton.OK,MessageBoxImage.Warning); _status.Text=ex.Message;}catch(Newtonsoft.Json.JsonException ex){MessageBox.Show("JSON解析失败："+ex.Message,"智能导入失败",MessageBoxButton.OK,MessageBoxImage.Warning); _status.Text="JSON解析失败："+ex.Message;}catch(System.Net.Http.HttpRequestException ex){MessageBox.Show("网络错误："+ex.Message,"智能导入失败",MessageBoxButton.OK,MessageBoxImage.Warning); _status.Text="网络错误："+ex.Message;}catch(Exception ex){MessageBox.Show(ex.Message,"智能导入失败",MessageBoxButton.OK,MessageBoxImage.Warning); _status.Text="导入失败："+ex.Message;} finally{_start.IsEnabled=true;_cancel.IsEnabled=false;if(_cts!=null){_cts.Dispose();_cts=null;}} }
    }
}
