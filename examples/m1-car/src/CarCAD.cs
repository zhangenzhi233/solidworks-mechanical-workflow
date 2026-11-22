using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

public sealed class CarCAD {
    public ISldWorks Sw;
    public IModelDoc2 Road,Bench,ViewDoc;
    public IComponent2 Body;
    public readonly Dictionary<int,IComponent2> C=new Dictionary<int,IComponent2>();
    readonly MathTransform[] zero=new MathTransform[4];
    static readonly int[] wheel={8,9,10,11},anchor={4,5,1,1};
    IMathUtility util;
    string folder;
    double servo=double.NaN,roll=double.NaN,px=double.NaN,py,heading;
    double[] last=new double[]{double.NaN,double.NaN,double.NaN,double.NaN};
    public int Instances,Types;
    public int OpenWarnings;
    public bool Lifted{get{return ViewDoc==Bench;}}
    static bool Same(string a,string b){return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
    static IComponent2[] Parts(IModelDoc2 d,bool top){return ((object[])((IAssemblyDoc)d).GetComponents(top)).Cast<IComponent2>().ToArray();}
    static double[] Data(MathTransform m){return (double[])m.ArrayData;}
    MathTransform Tr(double[] a){return (MathTransform)util.CreateTransform(a);}
    MathTransform Rx(double a){
        double c=Math.Cos(a),s=Math.Sin(a);return Tr(new[]{1d,0,0,0,c,s,0,-s,c,0,0,0,1,0,0,0});
    }
    MathTransform Rz(double a,double x,double y,double z){
        double c=Math.Cos(a),s=Math.Sin(a);return Tr(new[]{c,s,0,-s,c,0,0,0,1,x/1000,y/1000,z/1000,1,0,0,0});
    }
    public CarCAD(string file){
        file=Path.GetFullPath(file);folder=Path.GetDirectoryName(file);
        Sw=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application.32",true));
        var old=Sw.GetCurrentWorkingDirectory();
        try{
            Sw.SetCurrentWorkingDirectory(folder);Road=Open(file);((IAssemblyDoc)Road).ResolveAllLightWeightComponents(false);
            Body=Parts(Road,true).Single(c=>Path.GetFileName(c.GetPathName())=="EX-M1-活动底盘.SLDASM");
            Bench=Body.GetModelDoc2() as IModelDoc2;
            if(Bench==null)throw new IOException("活动底盘加载失败");
            foreach(var c in Parts(Road,false)){
                var model=c.GetModelDoc2() as IModelDoc2;
                if(model==null||!Same(Path.GetDirectoryName(model.GetPathName()),folder))throw new IOException("请先关闭从其他目录打开的同名运动版装配及零件。引用路径异常 "+c.Name2);
            }
            if(Bench.ConfigurationManager.ActiveConfiguration.Name!="受力联动")Bench.ShowConfiguration2("受力联动");
            UseGroups();
            var pp=Parts(Bench,false).Where(c=>c.GetPathName().EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase)).ToArray();
            Instances=pp.Length;Types=pp.Select(c=>c.GetPathName()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if(Instances!=268||Types!=91||C.Count!=12)throw new IOException("装配实例不全 "+Instances+" / "+Types+" / "+C.Count);
            util=(IMathUtility)Sw.GetMathUtility();
            for(int i=0;i<4;i++){
                int side=i%2*2-1;zero[i]=Rz(side<0?Math.PI:0,side*(i<2?19:75),0,30);
            }
            Sw.Visible=true;Sw.UserControl=true;Show(false);
        }finally{if(!String.IsNullOrEmpty(old))Sw.SetCurrentWorkingDirectory(old);}
    }
    IModelDoc2 Open(string path){
        int e=0,w=0;var d=Sw.GetOpenDocumentByName(path) as IModelDoc2;
        if(d==null)d=Sw.OpenDoc6(path,2,65,"",ref e,ref w) as IModelDoc2;
        if(d==null||!Same(d.GetPathName(),path)||e!=0||(w&~32)!=0)throw new IOException("打开失败 "+path+"，错误 "+e+"，警告 "+w);
        OpenWarnings=w;return d;
    }
    void UseGroups(){
        C.Clear();
        foreach(var c in Parts(Bench,true)){
            string name=Path.GetFileNameWithoutExtension(c.GetPathName());int n;
            const string groupPrefix="EX-动-M1-";
            if(name.StartsWith(groupPrefix,StringComparison.Ordinal)&&name.Length>=groupPrefix.Length+2&&int.TryParse(name.Substring(groupPrefix.Length,2),out n))C.Add(n,c);
        }
    }
    public void Show(bool lifted){
        int e=0;
        if(lifted){Open(Bench.GetPathName());}
        ViewDoc=lifted?Bench:Road;
        Sw.ActivateDoc3(ViewDoc.GetTitle(),false,0,ref e);
        if(e!=0)throw new IOException("激活视图失败 "+e);
        ((IModelView)ViewDoc.ActiveView).FrameState=1;
        ViewDoc.ClearSelection2(true);ViewDoc.GraphicsRedraw2();
    }
    public bool Active(){var d=Sw.ActiveDoc as IModelDoc2;return d!=null&&Same(d.GetPathName(),ViewDoc.GetPathName());}
    void Dim(string name,double degrees){
        var d=(IDimension)Bench.Parameter("D1@"+name);
        if(d==null||d.SetSystemValue3(Drive.Rad(degrees),1,null)!=0)throw new IOException("配合输入失败 "+name);
    }
    static bool Changed(double a,double b){return double.IsNaN(b)||Math.Abs(a-b)>.000001;}
    void Move(IComponent2 c,MathTransform tr){
        if(!c.SetTransformAndSolve2(tr))throw new IOException("无法求解 "+c.Name2);
    }
    public void Apply(Drive d,bool force=false){
        // A COM call may dispatch mouse events. Render all components from one sample.
        double targetServo=d.Servo,targetRoll=d.Roll,x=d.X,y=d.Y,yaw=d.Yaw;
        double[] travel=(double[])d.Travel.Clone();
        bool steering=force||Changed(targetServo,servo),bank=force||Changed(targetRoll,roll),dirty=false;
        if(steering)Dim("舵机输入角",90+targetServo);
        if(bank)Dim("前桥摆动输入",90-targetRoll);
        if(steering||bank){if(!Bench.EditRebuild3())throw new IOException("转向或摆桥重建失败");servo=targetServo;roll=targetRoll;dirty=true;}
        for(int i=0;i<4;i++){
            if(!force&&!steering&&!bank&&!Changed(travel[i],last[i]))continue;
            int side=i%2*2-1;double phase=-side*travel[i]/Drive.RadiusMm;
            var tr=Rx(phase).IMultiply(zero[i]).IMultiply(C[anchor[i]].Transform2);
            Move(C[wheel[i]],tr);last[i]=travel[i];dirty=true;
        }
        if(dirty&&!Road.EditRebuild3())throw new IOException("整车更新失败");
        if(force||Changed(x,px)||Changed(y,py)||Changed(yaw,heading)){
            Move(Body,Rz(-yaw,x,y,0));px=x;py=y;heading=yaw;dirty=true;
        }
        if(dirty)ViewDoc.GraphicsRedraw2();
    }
    public double[] Axis(Drive d){
        var view=((IModelView)ViewDoc.ActiveView).Transform;
        Func<double,double,double[]> map=(x,y)=>(double[])((MathPoint)((MathPoint)util.CreatePoint(new[]{x,y,0})).MultiplyTransform(view)).ArrayData;
        var a=map(d.X/1000,d.Y/1000);var b=map((d.X+Math.Sin(d.Yaw))/1000,(d.Y+Math.Cos(d.Yaw))/1000);
        return new[]{b[0]-a[0],b[1]-a[1]};
    }
    public double[] Read(){
        var t=Data(Body.Transform2);var b=Data(C[2].Transform2);
        var angles=new List<double>{t[9]*1000,t[10]*1000,Drive.Deg(-Math.Atan2(t[1],t[0])),Drive.Deg(Math.Atan2(b[6],b[8]))};
        foreach(int j in new[]{3,4,5}){var m=Data(C[j].Transform2.IMultiply(C[2].Transform2.IInverse()));angles.Add(Drive.Deg(Math.Atan2(m[1],m[0])));}
        for(int i=0;i<4;i++){
            var m=Data(C[wheel[i]].Transform2.IMultiply(C[anchor[i]].Transform2.IInverse()).IMultiply(zero[i].IInverse()));
            angles.Add(Drive.Deg(Math.Atan2(m[5],m[4])));
        }
        double worst=0;
        for(int i=0;i<2;i++){
            int side=i*2-1;
            var rod=C[6+i].Transform2;var knuckle=C[4+i].Transform2;var horn=C[3].Transform2;
            worst=Math.Max(worst,Distance(At(rod,0,0,0),At(horn,0,12,0)));
            worst=Math.Max(worst,Distance(At(rod,49,0,0),At(knuckle,-side*7,-16,0)));
        }
        angles.Add(worst);return angles.ToArray();
    }
    double[] At(MathTransform m,double x,double y,double z){
        return (double[])((MathPoint)((MathPoint)util.CreatePoint(new[]{x/1000,y/1000,z/1000})).MultiplyTransform(m)).ArrayData;
    }
    static double Distance(double[] a,double[] b){return 1000*Math.Sqrt(a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum());}
    public double WorldWitness(){
        var outer=((object[])Body.GetChildren()).Cast<IComponent2>().ToDictionary(q=>q.GetPathName());
        double worst=0;
        foreach(int j in new[]{1,2,3,4,5,6,7,8,9,10,11,12}){
            var group=outer[C[j].GetPathName()];
            var leaf=((object[])group.GetChildren()).Cast<IComponent2>().First();
            var relative=((object[])((IAssemblyDoc)C[j].GetModelDoc2()).GetComponents(true)).Cast<IComponent2>().Single(q=>q.Name2==leaf.Name2.Split('/').Last());
            var expected=relative.Transform2.IMultiply(C[j].Transform2).IMultiply(Body.Transform2);
            var actual=leaf.GetTotalTransform(false);
            foreach(var p in new[]{new[]{0d,0,0},new[]{30d,0,0},new[]{0d,30,0},new[]{0d,0,30}}){
                worst=Math.Max(worst,Distance(At(expected,p[0],p[1],p[2]),At(actual,p[0],p[1],p[2])));
            }
        }
        return worst;
    }
}
