using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NaiwaDesktop
{
    /// <summary>右键菜单的深色主题：圆角渐变面板、圆角悬停、细分隔线、琥珀色勾选与细箭头。</summary>
    internal static class PetMenuTheme
    {
        internal static readonly Color PanelTop=Color.FromArgb(44,47,54);
        internal static readonly Color PanelBottom=Color.FromArgb(28,30,36);
        internal static readonly Color Border=Color.FromArgb(62,66,76);
        internal static readonly Color Text=Color.FromArgb(234,236,240);
        internal static readonly Color TextDim=Color.FromArgb(132,137,146);
        internal static readonly Color Separator=Color.FromArgb(54,58,66);
        internal static readonly Color Accent=Color.FromArgb(255,199,92);
        internal static readonly Color Arrow=Color.FromArgb(158,163,173);
        internal static readonly Color HoverTop=Color.FromArgb(66,70,80);
        internal static readonly Color HoverBottom=Color.FromArgb(50,53,62);

        internal static GraphicsPath Round(RectangleF bounds,float radius)
        {
            float d=Math.Max(2f,radius*2f);
            GraphicsPath path=new GraphicsPath();
            path.AddArc(bounds.X,bounds.Y,d,d,180f,90f);
            path.AddArc(bounds.Right-d,bounds.Y,d,d,270f,90f);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0f,90f);
            path.AddArc(bounds.X,bounds.Bottom-d,d,d,90f,90f);
            path.CloseFigure();
            return path;
        }

        internal static float Ui()
        {
            try{using(Graphics g=Graphics.FromHwnd(IntPtr.Zero))return Math.Max(1f,g.DpiX/96f);}catch{return 1f;}
        }

        internal static void Style(ContextMenuStrip menu)
        {
            if(menu==null)return;
            float ui=Ui();
            menu.Font=new Font("Microsoft YaHei UI",10.5f,FontStyle.Regular,GraphicsUnit.Point);
            menu.BackColor=PanelBottom;
            menu.ForeColor=Text;
            menu.ShowImageMargin=false;
            menu.ShowCheckMargin=false;
            menu.DropShadowEnabled=true;
            menu.Renderer=new PetMenuRenderer(ui);
            menu.Padding=Padding.Empty;
            menu.Opened+=delegate{Fit(menu);Shape(menu,ui);Clamp(menu);};
            Decorate(menu,ui);
            Style(menu.Items,ui);
        }

        /// <summary>菜单按内容重算宽度后会顶出屏幕边缘，这里把它挪回工作区。</summary>
        internal static void Clamp(ToolStripDropDown down)
        {
            if(down==null||down.IsDisposed)return;
            Rectangle bounds=down.Bounds;
            Rectangle work=Screen.FromRectangle(bounds).WorkingArea;
            int dx=0,dy=0;
            int gap=(int)(10*Ui());
            if(bounds.Right+gap>work.Right)dx=work.Right-gap-bounds.Right;
            if(bounds.Left+dx<work.Left+gap)dx=work.Left+gap-bounds.Left;
            if(bounds.Bottom+gap>work.Bottom)dy=work.Bottom-gap-bounds.Bottom;
            if(bounds.Top+dy<work.Top+gap)dy=work.Top+gap-bounds.Top;
            if(dx!=0||dy!=0)down.Location=new Point(bounds.Left+dx,bounds.Top+dy);
        }

        /// <summary>挂上自绘的悬停底色、勾选与箭头。菜单项的 OnRender 交给系统，覆盖层画在最后。</summary>
        private static void Decorate(ToolStrip strip,float ui)
        {
            strip.Paint+=delegate(object s,PaintEventArgs e){DrawOverlay(strip,e.Graphics,ui);};
        }

        internal static void DrawOverlay(ToolStrip strip,Graphics g,float ui)
        {
            if(strip==null||strip.IsDisposed)return;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            int right=strip.Width;
            foreach(ToolStripItem item in strip.Items)
            {
                if(item is ToolStripSeparator||!item.Available)continue;
                Rectangle bounds=item.Bounds;
                if(bounds.Width<=0||bounds.Height<=0)continue;
                if(item.Selected&&item.Enabled)
                {
                    RectangleF inner=new RectangleF(bounds.X+2f*ui,bounds.Y+1f,bounds.Width-4f*ui,bounds.Height-2f);
                    if(inner.Width>3f&&inner.Height>3f)
                        using(GraphicsPath path=Round(inner,8f*ui))
                        using(LinearGradientBrush brush=new LinearGradientBrush(inner,HoverTop,HoverBottom,90f))
                            g.FillPath(brush,path);
                }
                ToolStripMenuItem entry=item as ToolStripMenuItem;
                if(entry==null)continue;
                if(entry.Checked)DrawCheck(g,bounds,right,ui);
                if(entry.HasDropDownItems)DrawArrow(g,bounds,right,ui);
            }
        }

        private static void DrawCheck(Graphics g,Rectangle bounds,int right,float ui)
        {
            float size=10f*ui;
            float x=right-20f*ui;
            float y=bounds.Top+bounds.Height/2f-size/2f-1f*ui;
            using(GraphicsPath path=new GraphicsPath())
            {
                path.AddLines(new PointF[]{new PointF(x,y+size*0.45f),new PointF(x+size*0.35f,y+size*0.85f),new PointF(x+size,y+size*0.12f)});
                using(Pen pen=new Pen(Accent,1.8f*ui))
                {
                    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                    g.DrawPath(pen,path);
                }
            }
        }

        private static void DrawArrow(Graphics g,Rectangle bounds,int right,float ui)
        {
            float cx=right-13f*ui,cy=bounds.Top+bounds.Height/2f,s=3.4f*ui;
            using(Pen pen=new Pen(Arrow,1.6f*ui))
            {
                pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                g.DrawLines(pen,new PointF[]{new PointF(cx-s*0.5f,cy-s),new PointF(cx+s*0.5f,cy),new PointF(cx-s*0.5f,cy+s)});
            }
        }

        /// <summary>改过字号和留白以后，弹出窗口的宽度会停留在旧值上，这里按文字实际宽度重新算一次。</summary>
        internal static void Fit(ToolStripDropDown down)
        {
            if(down==null||down.IsDisposed)return;
            try
            {
                // Outerv spacing lives on the items' Margin: ToolStripDropDownMenu ignores Padding while laying out.
                int width=0,height=0;
                foreach(ToolStripItem item in down.Items)
                {
                    if(!item.Available)continue;
                    width=Math.Max(width,TextRenderer.MeasureText(item.Text,item.Font).Width+item.Padding.Horizontal+item.Margin.Horizontal);
                    height+=item.Height+item.Margin.Vertical;
                }
                if(width<=0)return;
                width+=4;
                height+=4;
                down.MinimumSize=new Size(width,height);
                down.Size=new Size(width,height);
            }
            catch{}
        }

        internal static void Style(ToolStripItemCollection items,float ui)
        {
            int visible=0;ToolStripItem first=null,last=null;
            foreach(ToolStripItem item in items)if(item.Available){visible++;if(first==null)first=item;last=item;}
            foreach(ToolStripItem item in items)
            {
                int top=item==first?(int)(13*ui):0,bottom=item==last?(int)(13*ui):0;
                if(item is ToolStripSeparator)
                {
                    item.AutoSize=false;
                    item.Height=(int)(11*ui);
                    item.Margin=new Padding((int)(10*ui),0,(int)(10*ui),0);
                    continue;
                }
                item.Margin=new Padding((int)(10*ui),top,(int)(10*ui),bottom);
                item.Padding=new Padding((int)(4*ui),(int)(7*ui),(int)(28*ui),(int)(7*ui));
                item.ForeColor=Text;
                ToolStripMenuItem owner=item as ToolStripMenuItem;
                if(owner==null)continue;
                owner.DropDown.Font=new Font("Microsoft YaHei UI",10.5f,FontStyle.Regular,GraphicsUnit.Point);
                owner.DropDown.BackColor=PanelBottom;
                owner.DropDown.ForeColor=Text;
                ToolStripDropDownMenu sub=owner.DropDown as ToolStripDropDownMenu;
                if(sub!=null){sub.ShowImageMargin=false;sub.ShowCheckMargin=false;}
                owner.DropDown.DropShadowEnabled=true;
                owner.DropDown.Padding=Padding.Empty;
                owner.DropDown.Opened+=delegate{Fit(owner.DropDown);Shape(owner.DropDown,ui);Clamp(owner.DropDown);};
                Decorate(owner.DropDown,ui);
                Style(owner.DropDownItems,ui);
            }
        }

        /// <summary>把弹出窗口裁成圆角；Windows 的默认矩形阴影不会因此失效。</summary>
        internal static void Shape(ToolStripDropDown down,float ui)
        {
            if(down==null||down.IsDisposed)return;
            int width=down.Width,height=down.Height;
            if(width<=0||height<=0)return;
            int radius=Math.Max(6,(int)(12*ui));
            IntPtr handle=Native.CreateRoundRectRgn(0,0,width+1,height+1,radius,radius);
            if(handle==IntPtr.Zero)return;
            try{down.Region=Region.FromHrgn(handle);}
            catch{}
            finally{Native.DeleteObject(handle);}
        }
    }

    /// <summary>完全自绘：不继承 Professional 渲染器，避免系统配色渗进来。</summary>
    internal sealed class PetMenuRenderer:ToolStripRenderer
    {
        private readonly float ui;
        internal PetMenuRenderer(float ui){this.ui=ui;}

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics g=e.Graphics;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            int w=e.ToolStrip.Width,h=e.ToolStrip.Height;
            if(w<=2||h<=2)return;
            RectangleF bounds=new RectangleF(0f,0f,w,h);
            using(GraphicsPath path=PetMenuTheme.Round(new RectangleF(0.5f,0.5f,w-1f,h-1f),12f*ui))
            using(LinearGradientBrush brush=new LinearGradientBrush(bounds,PetMenuTheme.PanelTop,PetMenuTheme.PanelBottom,90f))
                g.FillPath(brush,path);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Graphics g=e.Graphics;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            int w=e.ToolStrip.Width,h=e.ToolStrip.Height;
            if(w<=2||h<=2)return;
            using(GraphicsPath path=PetMenuTheme.Round(new RectangleF(0.5f,0.5f,w-1.5f,h-1.5f),12f*ui))
            using(Pen pen=new Pen(PetMenuTheme.Border,1f))
                g.DrawPath(pen,path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor=e.Item.Enabled?(e.Item.Selected?Color.White:PetMenuTheme.Text):PetMenuTheme.TextDim;
            e.TextFont=new Font("Microsoft YaHei UI",10.5f,FontStyle.Regular,GraphicsUnit.Point);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Graphics g=e.Graphics;
            Rectangle bounds=e.Item.Bounds;
            int inset=(int)(14*ui);
            int y=bounds.Y+bounds.Height/2;
            using(Pen pen=new Pen(PetMenuTheme.Separator,1f))
                g.DrawLine(pen,bounds.X+inset,y,bounds.Right-inset,y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e){}
    }
}
