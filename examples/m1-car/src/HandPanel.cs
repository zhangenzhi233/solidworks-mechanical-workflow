using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using Environment=System.Environment;

public sealed class HandPanel : Form {
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rectangle r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    readonly CarCAD cad;
    readonly RadioButton push=new RadioButton(),steer=new RadioButton(),bank=new RadioButton();
    readonly Label instruction=new Label(),values=new Label();
    readonly Timer timer=new Timer();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly string log;
    Drive state=new Drive();
    Mouse mouse;
    bool dragging,flushPose,working,ready,reportStop;
    readonly uint hostProcess,selfProcess;
    Point start;
    double first,dx,dy,previous;
    void Log(string text){if(log!=null)File.AppendAllText(log,DateTime.Now.ToString("HH:mm:ss.fff")+" "+text+Environment.NewLine);}
    public HandPanel(CarCAD model,string logfile){
        cad=model;log=logfile;
        Text="M1 四轮车 · 手推与转向";Font=new Font("Microsoft YaHei UI",9);
        FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;ShowInTaskbar=true;StartPosition=FormStartPosition.Manual;ClientSize=new Size(312,216);
        hostProcess=(uint)cad.Sw.GetProcessID();selfProcess=(uint)Process.GetCurrentProcess().Id;TopMost=true;
        var h=new IntPtr(((IFrame)cad.Sw.Frame()).GetHWnd());Rectangle area;GetWindowRect(h,out area);
        var screen=Screen.FromHandle(h).WorkingArea;Location=new Point(Math.Max(screen.Left,area.Left+12),Math.Min(screen.Bottom-262,Math.Max(screen.Top,area.Top+150)));
        var heading=new Label(){Text="在 SOLIDWORKS 图形区按住左键拖动",Left=12,Top=10,Width=294};Controls.Add(heading);
        int left=12;
        foreach(var p in new[]{Tuple.Create(push,"推车"),Tuple.Create(steer,"转向"),Tuple.Create(bank,"举升摆桥")}){
            p.Item1.Text=p.Item2;p.Item1.SetBounds(left,35,91,24);left+=94;Controls.Add(p.Item1);
            p.Item1.CheckedChanged+=(s,e)=>{if(((RadioButton)s).Checked&&ready)RunSafe(ChangeMode);};
        }
        instruction.SetBounds(12,65,288,36);Controls.Add(instruction);
        var pulse=new Button(){Text="短推 1 N",Left=12,Top=107,Width=92,Height=28};
        pulse.Click+=(s,e)=>RunSafe(()=>{if(!push.Checked){push.Checked=true;ChangeMode();}state.Brake=false;state.Push=.2;reportStop=true;previous=clock.Elapsed.TotalSeconds;Log("pulse 1 N for 0.200 s");});Controls.Add(pulse);
        var brake=new Button(){Text="制动",Left=111,Top=107,Width=80,Height=28};
        brake.Click+=(s,e)=>{state.Brake=true;state.Held=false;state.Push=0;dragging=false;};Controls.Add(brake);
        var clear=new Button(){Text="复位",Left=198,Top=107,Width=88,Height=28};
        clear.Click+=(s,e)=>RunSafe(()=>{state=new Drive();state.Steer(0);dragging=false;cad.Apply(state,true);previous=clock.Elapsed.TotalSeconds;Status();Log("reset");});Controls.Add(clear);
        values.SetBounds(12,146,292,63);Controls.Add(values);
        push.Checked=true;ready=true;state.Steer(0);ChangeMode();cad.Apply(state,true);Status();
        timer.Interval=40;timer.Tick+=(s,e)=>RunSafe(Tick);
        Shown+=(s,e)=>{previous=clock.Elapsed.TotalSeconds;timer.Start();};
        FormClosed+=(s,e)=>{timer.Stop();Unbind();try{var neutral=new Drive();neutral.Steer(0);cad.Show(false);cad.Apply(neutral,true);}catch(Exception ex){Log(ex.Message);}};
    }
    void RunSafe(Action action){
        if(working)return;working=true;
        try{action();}catch(Exception ex){timer.Stop();instruction.Text=ex.Message;Log("error "+ex);}
        finally{working=false;}
    }
    void Unbind(){
        if(mouse==null)return;
        mouse.MouseLBtnDownNotify-=Down;mouse.MouseMoveNotify-=MoveMouse;mouse.MouseLBtnUpNotify-=Up;mouse=null;
    }
    void ChangeMode(){
        Unbind();dragging=false;state.Held=false;state.Push=0;
        if(bank.Checked){state.Speed=0;state.Brake=false;}
        else{state.Roll=0;}
        cad.Show(bank.Checked);
        mouse=(Mouse)((IModelView)cad.ViewDoc.ActiveView).GetMouse();
        mouse.MouseLBtnDownNotify+=Down;mouse.MouseMoveNotify+=MoveMouse;mouse.MouseLBtnUpNotify+=Up;
        instruction.Text=bank.Checked?"在底盘视图中向上或向下拖，观察前桥绕纵轴摆动。":steer.Checked?"左右拖动图中舵臂或车头，观察两根拉杆和内外前轮。":"沿后轮朝车头的方向拖并松手，观察滑行。反方向拖可倒车。";
        flushPose=true;previous=clock.Elapsed.TotalSeconds;Log("mode "+(bank.Checked?"bank":steer.Checked?"steer":"push"));
    }
    int Down(int x,int y,int w){
        if(!ready||!cad.Active())return 0;
        var axis=cad.Axis(state);dx=axis[0];dy=axis[1];
        if(push.Checked&&dx*dx+dy*dy<.01){instruction.Text="车头正对视线，旋转视图后再沿车长拖动。";return 0;}
        start=new Point(x,y);first=bank.Checked?state.Roll:steer.Checked?state.Servo:state.S;
        state.HandTarget=state.S;state.Brake=false;state.Push=0;state.Held=push.Checked;dragging=true;
        reportStop=push.Checked;previous=clock.Elapsed.TotalSeconds;Log("press "+start+" pixels/mm "+dx.ToString("F3")+" "+dy.ToString("F3"));return 0;
    }
    void Target(int x,int y){
        if(!dragging)return;
        if(push.Checked)state.HandTarget=first+((x-start.X)*dx+(y-start.Y)*dy)/(dx*dx+dy*dy);
        else if(steer.Checked)state.Steer(first+(x-start.X)*.30);
        else state.Roll=Drive.Clamp(first-(y-start.Y)*.045,-3,3);
    }
    void Elapse(){
        double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.20,Math.Max(0,now-previous));previous=now;
        if(state.Held||state.Push>0||Math.Abs(state.Speed)>1e-8)state.Advance(dt);
    }
    int MoveMouse(int x,int y,int w){if(dragging){Target(x,y);Elapse();}return 0;}
    int Up(int x,int y,int w){
        if(dragging){
            Target(x,y);Elapse();dragging=false;state.Held=false;flushPose=true;
            Log(String.Format(CultureInfo.InvariantCulture,"release at {0},{1} path={2:F4} v={3:F4} steer={4:F3} roll={5:F3}",x,y,state.S,state.Speed,state.Servo,state.Roll));
        }return 0;
    }
    void Tick(){
        uint active;GetWindowThreadProcessId(GetForegroundWindow(),out active);
        bool onTop=active==hostProcess||active==selfProcess;
        if(TopMost!=onTop)TopMost=onTop;
        if(!cad.Active()){dragging=false;state.Held=false;previous=clock.Elapsed.TotalSeconds;return;}
        Elapse();bool full=flushPose;flushPose=false;cad.Apply(state,full);
        if(full&&log!=null)Log("native "+String.Join(", ",Array.ConvertAll(cad.Read(),n=>n.ToString("F4",CultureInfo.InvariantCulture))));
        if(reportStop&&!state.Held&&!dragging&&state.Push==0&&state.Speed==0){
            reportStop=false;if(log!=null)Log("stop native "+String.Join(", ",Array.ConvertAll(cad.Read(),n=>n.ToString("F5",CultureInfo.InvariantCulture))));
        }
        Status();
    }
    void Status(){
        values.Text=String.Format("后轴中心路程 {0:F1} mm　速度 {1:F1} mm/s\n舵臂 {2:F1}°　左前轮 {3:F1}°　右前轮 {4:F1}°\n前桥摆角 {5:F1}°　车身航向 {6:F1}°",state.S,state.Speed,state.Servo,Drive.Deg(state.Heading[0]),Drive.Deg(state.Heading[1]),state.Roll,Drive.Deg(state.Yaw));
    }
}
