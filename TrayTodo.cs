using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[DataContract] public class Todo {
 [DataMember] public string Id = Guid.NewGuid().ToString();
 [DataMember] public string Title;
 [DataMember] public string Subject;
 [DataMember] public bool InProgress;
 [DataMember] public string ReminderStamp;
 [DataMember] public DateTime? Due;
 [DataMember] public DateTime? Completed;
 [DataMember] public DateTime Created = DateTime.Now;
}
public class Store {
 public string Path;
 public Store(string path) { Path = path; }
 public List<Todo> Load() {
  if (!File.Exists(Path)) return new List<Todo>();
  using (var f = File.OpenRead(Path)) {
   var result = (List<Todo>)new DataContractJsonSerializer(typeof(List<Todo>)).ReadObject(f);
   if (result == null || result.Any(t => t == null || String.IsNullOrWhiteSpace(t.Title))) throw new InvalidDataException("잘못된 작업 데이터입니다.");
   return result;
  }
 }
 public void Save(List<Todo> items) {
  Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
  string temp = Path + ".tmp";
  using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
   new DataContractJsonSerializer(typeof(List<Todo>)).WriteObject(f, items); f.Flush(true);
  }
  if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak"); else File.Move(temp, Path);
 }
}
public class Program {
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
 [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
 [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
 [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
 static Mutex mutex;
 Window window; Forms.NotifyIcon tray; List<Todo> items; Store store; bool completedPage; string editId; bool exiting;
 ComboBox subjectInput, progressInput, folderFilter; bool refreshing; DateTime nextReminder = DateTime.MinValue;
 TextBox input; DatePicker due; StackPanel list; TextBlock status; bool test;
 T Find<T>(string name) where T: class { return window.FindName(name) as T; }
 Brush Brush(string key) { return (Brush)window.FindResource(key); }
 [STAThread] public static void Main(string[] args) {
  if (args.Contains("--self-test")) { SelfTest(); return; }
  bool first; mutex = new Mutex(true, "Local\\TrayTodo.06060"+(args.Contains("--ui-test") ? ".test" : ""), out first);
  if (!first) { File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTodo", "show.signal"), "show"); return; }
  try { new Program().Run(args); } catch(Exception e) { MessageBox.Show(e.ToString(), "오늘 할 일 오류"); }
  finally { mutex.ReleaseMutex(); mutex.Dispose(); }
 }
 void Run(string[] args) {
  test = args.Contains("--ui-test");
  string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTodo");
  Directory.CreateDirectory(folder); store = new Store(System.IO.Path.Combine(folder, test ? "ui-test.json" : "tasks.json"));
  try { items = store.Load(); } catch (Exception e) { MessageBox.Show("저장 파일을 읽지 못했습니다. 기존 파일은 보존됩니다.\n" + store.Path + "\n" + e.Message, "오늘 할 일"); return; }
  var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
  using(var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window = (Window)XamlReader.Load(stream);
  if(test) window.Title="오늘 할 일 · 테스트";
  input = Find<TextBox>("TaskInput"); due = Find<DatePicker>("DueInput"); list = Find<StackPanel>("TaskList"); status = Find<TextBlock>("Status");
  subjectInput=Find<ComboBox>("SubjectInput"); progressInput=Find<ComboBox>("ProgressInput"); folderFilter=Find<ComboBox>("FolderFilter");
  progressInput.ItemsSource=new[]{"시작 전","작업 중"}; progressInput.SelectedIndex=0;
  folderFilter.SelectionChanged += delegate { if(!refreshing) Render(); };
  Find<Button>("AddButton").Click += delegate { Add(); };
  Find<Border>("Composer").PreviewKeyDown += delegate(object s, KeyEventArgs e) {
   if(e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
   if(subjectInput.IsDropDownOpen || progressInput.IsDropDownOpen || due.IsDropDownOpen) return;
   // Ignore the IME composition key; only a committed Enter submits the form.
   e.Handled = true;
   Add();
  };
  Find<Button>("ClearDate").Click += delegate { due.SelectedDate = null; due.Text = ""; };
  Find<Button>("CancelEdit").Click += delegate { ResetEditor(); };
  Find<Button>("HideButton").Click += delegate { window.Hide(); };
  Find<Button>("ActiveTab").Click += delegate { completedPage = false; Render(); };
  Find<Button>("DoneTab").Click += delegate { completedPage = true; ResetEditor(); Render(); };
  window.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if(e.Key == Key.Escape) window.Hide(); };
  window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; window.Hide(); } };
  window.SourceInitialized += delegate {
   var handle = new WindowInteropHelper(window).Handle;
   int corner = 2; DwmSetWindowAttribute(handle,33,ref corner,4);
   new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8) }.SetFor(window);
   bool registered = RegisterHotKey(handle, 1, 0x4000 | 1 | 2, 0x54);
   if (!registered) status.Text = "단축키 사용 중 · 트레이 아이콘으로 열어주세요";
   HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled) { if(m == 0x0312) { Toggle(); handled = true; } return IntPtr.Zero; });
  };
  CreateTray();
  var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
  timer.Tick += delegate {
   if(!test && DateTime.Now >= nextReminder) { nextReminder=DateTime.Now.AddMinutes(1); CheckReminders(); }
   string signal = System.IO.Path.Combine(folder,"show.signal");
   if (File.Exists(signal)) { File.Delete(signal); Open(); }
   if (window.IsVisible) Find<TextBlock>("Today").Text = DateTime.Now.ToString("M월 d일 dddd", new System.Globalization.CultureInfo("ko-KR"));
  }; timer.Start();
  app.Exit += delegate { timer.Stop(); UnregisterHotKey(new WindowInteropHelper(window).Handle,1); tray.Visible = false; tray.Dispose(); };
  Theme(); Render();
  if (!args.Contains("--background")) Open(); else { new WindowInteropHelper(window).EnsureHandle(); }
  app.Run();
 }
 void Theme() {
  bool dark = true;
  using(var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) if(key != null) dark = Convert.ToInt32(key.GetValue("SystemUsesLightTheme",0)) == 0;
  string[] keys = {"Panel","Card","Text","Muted","Line","Accent"};
  string[] colors = dark ? new[]{"#202020","#2C2C2C","#F5F5F5","#B5B5B5","#454545","#9ACBFF"} : new[]{"#F3F3F3","#FFFFFF","#191919","#626262","#D5D5D5","#82BDF8"};
  for(int i=0;i<keys.Length;i++) window.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
  int d = dark ? 1 : 0; var h = new WindowInteropHelper(window).Handle; if(h != IntPtr.Zero) DwmSetWindowAttribute(h,20,ref d,4);
 }
 void Open() {
  Theme(); Render(); window.Show();
  var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
  var source = PresentationSource.FromVisual(window); var transform = source.CompositionTarget.TransformFromDevice;
  var bottom = transform.Transform(new Point(screen.Right,screen.Bottom)); var top = transform.Transform(new Point(screen.Left,screen.Top));
  window.Height = Math.Min(740, bottom.Y-top.Y-24); window.Left = Math.Max(top.X,bottom.X-window.Width-12); window.Top = Math.Max(top.Y,bottom.Y-window.Height-12);
  window.Activate();
 }
 void Toggle() { if(window.IsVisible) window.Hide(); else Open(); }
 void CreateTray() {
  tray = new Forms.NotifyIcon { Text = "오늘 할 일 · Ctrl+Alt+T", Visible = true };
  using(var bmp = new System.Drawing.Bitmap(32,32)) {
   using(var g = System.Drawing.Graphics.FromImage(bmp)) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(System.Drawing.Color.Transparent); using(var p = new System.Drawing.Pen(System.Drawing.Color.FromArgb(154,203,255),3)) { g.DrawEllipse(p,3,3,26,26); g.DrawLines(p,new[]{new System.Drawing.Point(9,16),new System.Drawing.Point(14,21),new System.Drawing.Point(24,11)}); } }
   var h = bmp.GetHicon(); tray.Icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); DestroyIcon(h);
  }
  tray.BalloonTipClicked += delegate { completedPage=false; folderFilter.SelectedIndex=0; Open(); };
  tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if(e.Button == Forms.MouseButtons.Left) Toggle(); };
  var menu = new Forms.ContextMenuStrip(); menu.Items.Add("할 일 열기", null, delegate { Open(); });
  var startup = new Forms.ToolStripMenuItem("Windows 시작 시 실행") { CheckOnClick = true };
  using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.Checked = key != null && key.GetValue("TrayTodo") != null;
  startup.Click += delegate { try { using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if(startup.Checked) key.SetValue("TrayTodo", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --background"); else key.DeleteValue("TrayTodo",false); } } catch(Exception e) { startup.Checked = !startup.Checked; MessageBox.Show(e.Message); } };
  menu.Items.Add("알림 테스트",null,delegate { tray.ShowBalloonTip(10000,"오늘 할 일 · 알림 테스트","마감 이틀 전부터 알림을 보냅니다. 시작 전인 작업은 경고로 표시됩니다.",Forms.ToolTipIcon.Info); });
  menu.Items.Add(startup); menu.Items.Add("종료", null, delegate { exiting = true; Application.Current.Shutdown(); }); tray.ContextMenuStrip = menu;
 }
 bool Commit(Action change) {
  var before = items.Select(t => new Todo { Id=t.Id,Title=t.Title,Due=t.Due,Completed=t.Completed,Created=t.Created,Subject=t.Subject,InProgress=t.InProgress,ReminderStamp=t.ReminderStamp }).ToList();
  try { change(); store.Save(items); status.Text = "저장됨 · " + DateTime.Now.ToString("HH:mm"); Render(); return true; }
  catch(Exception e) { items = before; Render(); status.Text = "저장하지 못했습니다. 다시 시도해 주세요."; MessageBox.Show(e.Message,"저장 오류"); return false; }
 }
 void Add() {
  if(String.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; }
  DateTime parsed;
  if(!String.IsNullOrWhiteSpace(due.Text) && !DateTime.TryParse(due.Text, out parsed)) { status.Text = "올바른 마감일을 선택해 주세요."; return; }
  DateTime? date = String.IsNullOrWhiteSpace(due.Text) ? (DateTime?)null : DateTime.Parse(due.Text).Date;
  string title = input.Text.Trim(); string subject=subjectInput.Text.Trim(); if(subject.Length>60) { status.Text="과목 이름은 60자 이내로 입력해 주세요."; return; } bool progressing=progressInput.SelectedIndex==1;
  if(Commit(delegate { if(editId == null) items.Add(new Todo { Title=title,Due=date,Subject=subject,InProgress=progressing }); else { var t=items.First(x=>x.Id==editId); t.Title=title; t.Due=date; t.Subject=subject; t.InProgress=progressing; } })) { ResetEditor(); input.Focus(); }
 }
 void ResetEditor() { progressInput.SelectedIndex=0; editId=null; input.Clear(); due.SelectedDate=null; due.Text=""; Find<TextBlock>("EditorLabel").Text="새 할 일"; Find<Button>("AddButton").Content="추가"; Find<Button>("CancelEdit").Visibility=Visibility.Collapsed; }
 void Render() {
  refreshing=true;
  string selected=folderFilter.SelectedItem as string; string typed=subjectInput.Text;
  var subjects=items.Select(t=>SubjectName(t)).Distinct().OrderBy(x=>x).ToList();
  subjectInput.ItemsSource=subjects.Where(x=>x!="미분류").ToList(); subjectInput.Text=typed;
  folderFilter.ItemsSource=new[]{"전체 과목"}.Concat(subjects).ToList(); folderFilter.SelectedItem=selected!=null && (selected=="전체 과목" || subjects.Contains(selected)) ? selected : "전체 과목";
  refreshing=false;
  Find<TextBlock>("Today").Text = DateTime.Now.ToString("M월 d일 dddd",new System.Globalization.CultureInfo("ko-KR"));
  Find<Button>("ActiveTab").Content = "할 일  " + items.Count(t=>!t.Completed.HasValue);
  Find<Button>("DoneTab").Content = "완료  " + items.Count(t=>t.Completed.HasValue);
  Find<Button>("ActiveTab").Foreground = Brush(completedPage ? "Muted" : "Accent"); Find<Button>("DoneTab").Foreground = Brush(completedPage ? "Accent" : "Muted");
  Find<Border>("Composer").Visibility = completedPage ? Visibility.Collapsed : Visibility.Visible;
  list.Children.Clear();
  var visible = completedPage ? items.Where(t=>t.Completed.HasValue).OrderByDescending(t=>t.Completed).ToList() : items.Where(t=>!t.Completed.HasValue).OrderBy(t=>t.Due ?? DateTime.MaxValue).ThenBy(t=>t.Created).ToList();
  if(folderFilter.SelectedIndex>0) visible=visible.Where(t=>SubjectName(t)==(string)folderFilter.SelectedItem).ToList();
  if(visible.Count==0) { list.Children.Add(new TextBlock { Text=completedPage ? "아직 완료한 작업이 없어요.\n완료한 할 일은 여기에 모입니다." : "할 일이 없는 여유로운 하루\n위에서 새 할 일을 추가해 보세요.", TextAlignment=TextAlignment.Center, Foreground=Brush("Muted"), Margin=new Thickness(0,44,0,0), LineHeight=26 }); return; }
  foreach(var item in visible) {
   var t=item; var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
   var check=new CheckBox { IsChecked=t.Completed.HasValue, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,5,12,0), ToolTip=t.Completed.HasValue ? "할 일로 되돌리기" : "완료하기" }; System.Windows.Automation.AutomationProperties.SetName(check,(t.Completed.HasValue ? "되돌리기: " : "완료: ")+t.Title);
   RoutedEventHandler toggle = delegate { Commit(delegate { t.Completed=check.IsChecked == true ? (DateTime?)DateTime.Now : null; }); }; check.Checked += toggle; check.Unchecked += toggle; grid.Children.Add(check);
   var content=new StackPanel(); Grid.SetColumn(content,1); grid.Children.Add(content);
   content.Children.Add(new TextBlock { Text=SubjectName(t), FontWeight=FontWeights.Bold, Foreground=Brush("Accent"), Margin=new Thickness(0,0,0,6), TextWrapping=TextWrapping.Wrap });
   var title=new TextBlock { Text=t.Title, TextWrapping=TextWrapping.Wrap, Foreground=Brush("Text"), FontSize=14 }; if(t.Completed.HasValue) title.TextDecorations=TextDecorations.Strikethrough; content.Children.Add(title);
   if(!completedPage) {
    var state=new ComboBox { ItemsSource=new[]{"시작 전","작업 중"}, SelectedIndex=t.InProgress ? 1 : 0, Margin=new Thickness(0,8,0,0), Width=112, HorizontalAlignment=HorizontalAlignment.Left };
    System.Windows.Automation.AutomationProperties.SetName(state,"진행 상태: "+t.Title);
    state.SelectionChanged += delegate { Commit(delegate { t.InProgress=state.SelectedIndex==1; }); }; content.Children.Add(state);
    if(t.Due.HasValue && t.Due.Value.Date<=DateTime.Today.AddDays(2) && !t.InProgress) content.Children.Add(new TextBlock { Text="⚠ 마감 임박 · 아직 시작 전", Foreground=new SolidColorBrush(Color.FromRgb(244,174,113)), FontSize=12, Margin=new Thickness(0,7,0,0) });
   }
   string label="마감일 없음"; bool overdue=false;
   if(t.Due.HasValue) { var days=(t.Due.Value.Date-DateTime.Today).Days; overdue=days<0 && !t.Completed.HasValue; label="마감 " + t.Due.Value.ToString("yyyy.MM.dd") + (overdue ? " · 기한 지남" : days==0 ? " · 오늘" : days==1 ? " · 내일" : ""); }
   content.Children.Add(new TextBlock { Text=label, Foreground=overdue ? new SolidColorBrush(Color.FromRgb(244,155,155)) : Brush("Muted"), FontSize=11, Margin=new Thickness(0,7,0,0) });
   if(t.Completed.HasValue) content.Children.Add(new TextBlock { Text="완료 " + t.Completed.Value.ToString("MM.dd HH:mm"), Foreground=Brush("Muted"), FontSize=11, Margin=new Thickness(0,4,0,0) });
   if(!completedPage) { var edit=new Button { Content="···", ToolTip="내용 / 마감일 수정", Padding=new Thickness(6,2,6,2), VerticalAlignment=VerticalAlignment.Top }; Grid.SetColumn(edit,2); grid.Children.Add(edit); edit.Click += delegate { editId=t.Id; input.Text=t.Title; due.SelectedDate=t.Due; subjectInput.Text=t.Subject ?? ""; progressInput.SelectedIndex=t.InProgress ? 1 : 0; Find<TextBlock>("EditorLabel").Text="할 일 수정"; Find<Button>("AddButton").Content="저장"; Find<Button>("CancelEdit").Visibility=Visibility.Visible; input.Focus(); }; }
   list.Children.Add(new Border { Child=grid, Background=Brush("Card"), CornerRadius=new CornerRadius(7), Padding=new Thickness(12), Margin=new Thickness(0,0,0,7) });
  }
 }
 static string SubjectName(Todo t) { return String.IsNullOrWhiteSpace(t.Subject) ? "미분류" : t.Subject; }
 public static string ReminderKey(Todo t,DateTime today) { return today.ToString("yyyyMMdd")+"|"+(t.Due.HasValue?t.Due.Value.ToString("yyyyMMdd"):""); }
 public static bool NeedsReminder(Todo t,DateTime today) { return !t.Completed.HasValue && t.Due.HasValue && t.Due.Value.Date<=today.Date.AddDays(2) && t.ReminderStamp!=ReminderKey(t,today); }
 void CheckReminders() {
  var pending=items.Where(t=>NeedsReminder(t,DateTime.Today)).OrderBy(t=>t.Due).ToList(); if(pending.Count==0) return;
  bool warning=pending.Any(t=>!t.InProgress);
  string body=String.Join("\n",pending.Take(3).Select(t=>"["+SubjectName(t)+"] "+t.Title+" · "+t.Due.Value.ToString("MM.dd")+" · "+(t.InProgress?"작업 중":"시작 전")));
  if(body.Length>190) body=body.Substring(0,190)+"…";
  body+="\n총 "+pending.Count+"건 · 클릭해서 확인";
  var stamps=pending.Select(t=>t.ReminderStamp).ToArray();
  try {
   tray.ShowBalloonTip(15000,warning?"⚠ 마감 임박 · 시작 전인 작업이 있어요":"마감일이 다가오고 있어요",body,warning?Forms.ToolTipIcon.Warning:Forms.ToolTipIcon.Info);
   foreach(var t in pending) t.ReminderStamp=ReminderKey(t,DateTime.Today);
   store.Save(items); if(window.IsVisible) Render();
  } catch(Exception) { for(int i=0;i<pending.Count;i++) pending[i].ReminderStamp=stamps[i]; status.Text="알림 기록을 저장하지 못했습니다. 다음 점검 때 재시도합니다."; }
 }
 static void SelfTest() {
  string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"TrayTodo-Test-"+Guid.NewGuid()); var s=new Store(System.IO.Path.Combine(dir,"tasks.json"));
  var rows=new List<Todo>{new Todo { Title="한글 테스트 <>&",Subject="인공지능",InProgress=true,Due=new DateTime(2026,11,3) }}; s.Save(rows); var loaded=s.Load();
  if(loaded.Count!=1 || loaded[0].Title!=rows[0].Title || loaded[0].Due!=rows[0].Due) throw new Exception("Round trip failed");
  loaded[0].Completed=DateTime.Now; s.Save(loaded); if(!s.Load()[0].Completed.HasValue) throw new Exception("Complete failed");
  loaded[0].Completed=null; s.Save(loaded); if(s.Load()[0].Completed.HasValue || !File.Exists(s.Path+".bak")) throw new Exception("Restore failed");
  if(s.Load()[0].Subject!="인공지능" || !s.Load()[0].InProgress) throw new Exception("Metadata persistence failed");
  var day=new DateTime(2026,11,3); var task=new Todo {Title="알림",Due=day.AddDays(3)};
  if(NeedsReminder(task,day)) throw new Exception("Early notification"); task.Due=day.AddDays(2);
  if(!NeedsReminder(task,day)) throw new Exception("D-2 missed"); task.ReminderStamp=ReminderKey(task,day);
  if(NeedsReminder(task,day)) throw new Exception("Duplicate notification"); if(!NeedsReminder(task,day.AddDays(1))) throw new Exception("Next day missed");
  task.Completed=day; if(NeedsReminder(task,day.AddDays(1))) throw new Exception("Completed notification"); task.Completed=null; task.Due=null;
  if(NeedsReminder(task,day)) throw new Exception("Undated notification");
  File.WriteAllText(s.Path,"[{\"Title\":\"legacy\"}]"); var legacy=s.Load()[0]; if(legacy.InProgress || SubjectName(legacy)!="미분류") throw new Exception("Legacy migration");
  File.WriteAllText(s.Path,"invalid"); bool rejected=false; try { s.Load(); } catch { rejected=true; } if(!rejected) throw new Exception("Corruption not detected");
  File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-results.txt"),"PASS: Korean text/date persistence, completion, restore, atomic backup, corrupt-file protection, subjects/status persistence, legacy migration, D-2 reminder boundaries and deduplication.");
  Directory.Delete(dir,true);
 }
}
static class ChromeExtension { public static void SetFor(this System.Windows.Shell.WindowChrome chrome,Window window) { System.Windows.Shell.WindowChrome.SetWindowChrome(window,chrome); } }
