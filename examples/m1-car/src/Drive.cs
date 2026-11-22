using System;

// Distances are in mm, angles in radians except for the servo input, forces in N.
public sealed class Drive {
    public const double MassKg=1.0, ResistanceN=.10, RadiusMm=30, Wheelbase=175, HalfTrack=75, KingOffset=56;
    public double X,Y,Yaw,S,Speed,Time,Servo,Roll,HandTarget,Push,Force,Curvature;
    public bool Held,Brake;
    public readonly double[] Heading=new double[2],Travel=new double[4];
    public static double Rad(double d){return d*Math.PI/180;}
    public static double Deg(double a){return a*180/Math.PI;}
    public static double Clamp(double x,double a,double b){return Math.Max(a,Math.Min(b,x));}

    // Intersect each steering-arm circle with its constant-length tie rod.
    public void Steer(double degrees){
        Servo=Clamp(degrees,-38,38);
        double a=Rad(Servo),tx=-12*Math.Sin(a),ty=147+12*Math.Cos(a);
        Curvature=0;
        for(int i=0;i<2;i++){
            int side=i*2-1;
            double dx=tx-side*KingOffset,dy=ty-Wheelbase,vx=-side*7,vy=-16;
            double len=49,arm2=vx*vx+vy*vy,q2=dx*dx+dy*dy;
            double beta=Math.Acos(Clamp((arm2+q2-len*len)/(2*Math.Sqrt(arm2*q2)),-1,1));
            double baseAngle=Math.Atan2(dy,dx)-Math.Atan2(vy,vx);
            double first=Math.IEEERemainder(baseAngle+beta,2*Math.PI),second=Math.IEEERemainder(baseAngle-beta,2*Math.PI);
            Heading[i]=-(Math.Abs(first)<Math.Abs(second)?first:second);
            double t=Math.Tan(Heading[i]);
            Curvature+=.5*t/(Wheelbase+side*KingOffset*t);
        }
    }
    public void RollPath(double ds){
        if(ds==0)return;
        double old=Yaw,k=Curvature;
        Yaw+=k*ds;
        if(Math.Abs(k)<1e-10){X+=ds*Math.Sin(old);Y+=ds*Math.Cos(old);}
        else{X+=(Math.Cos(old)-Math.Cos(Yaw))/k;Y+=(Math.Sin(Yaw)-Math.Sin(old))/k;}
        S+=ds;
        for(int i=0;i<2;i++){
            int side=i*2-1;
            double a=Heading[i],x=side*(KingOffset+(HalfTrack-KingOffset)*Math.Cos(a));
            double y=Wheelbase-side*(HalfTrack-KingOffset)*Math.Sin(a);
            Travel[i]+=ds*(k*y*Math.Sin(a)+(1-k*x)*Math.Cos(a));
            Travel[i+2]+=ds*(1-k*side*HalfTrack);
        }
    }
    public void Advance(double dt){
        while(dt>1e-9){
            double h=Math.Min(.001,dt);
            if(Push>1e-9)h=Math.Min(h,Push);
            Force=Held?Clamp(.025*(HandTarget-S)-.003*Speed,-1.5,1.5):(Push>1e-9?1.0:0.0);
            double resistance=ResistanceN+(Brake?1:0);
            double direction=Math.Abs(Speed)>1e-8?Math.Sign(Speed):Math.Sign(Force);
            double a=(Force-direction*resistance)*1000/MassKg;
            if(Math.Abs(Speed)<=1e-8&&Math.Abs(Force)<=resistance){Speed=0;a=0;}
            if(Speed*a<0&&Math.Abs(Speed/a)<h)h=Math.Abs(Speed/a);
            RollPath(Speed*h+.5*a*h*h);
            Speed+=a*h;if(Math.Abs(Speed)<1e-8)Speed=0;
            Push=Math.Max(0,Push-h);Time+=h;dt-=h;
        }
    }
}
