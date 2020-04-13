using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Urban_Simulator
{
    public class UrbanSimulatorCommand : Command
    {
        public UrbanSimulatorCommand()
        {
            // Rhino only creates one instance of each command class defined in a
            // plug-in, so it is safe to store a refence in a static property.
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static UrbanSimulatorCommand Instance
        {
            get; private set;
        }

        ///<returns>The command name as it appears on the Rhino command line.</returns>
        public override string EnglishName
        {
            get { return "UrbanSimulator"; }
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            RhinoApp.WriteLine("The Urban Simulator has begun.");

            urbanModel theUrbanModel = new urbanModel();

            if (!getPrecinct(theUrbanModel))               //ask user to select surface
                return Result.Failure;

            RhinoDoc.ActiveDoc.Views.RedrawEnabled.Equals(false);

            generateRoadNetwork(theUrbanModel);                  //generate road 
            createBlocks(theUrbanModel);                         //create blocks using road 
            subdivideBlocks(theUrbanModel, 30, 30, 50, 30);                      //subdivide blocks into 
            //instantiateBuildings(theUrbanModel);                 //place buildings on each block

            RhinoDoc.ActiveDoc.Views.RedrawEnabled.Equals(true);

            RhinoApp.WriteLine("The Urban Simulator is complete.");

            return Result.Success;
        }

        public bool getPrecinct(urbanModel model)
        {
            GetObject obj = new GetObject();

            obj.GeometryFilter = Rhino.DocObjects.ObjectType.Surface;
            obj.SetCommandPrompt("Please select surface for precinct");

            GetResult res = obj.Get();

            if (res != GetResult.Object)
            {
                RhinoApp.WriteLine("Failed to select surface.");
                return false;
            }

            if(obj.ObjectCount == 1)
                model.precinctSrf = obj.Object(0).Surface();

            return true;
        }

        public bool generateRoadNetwork(urbanModel model)
        {
            var iterationcount = 5;

            Random roadRand = new Random();

            var obstCrvs = model.precinctSrf.ToBrep().DuplicateNakedEdgeCurves(true, false).ToList();
            var borderNum = obstCrvs.Count;

            if (borderNum > 0)
            {
                Random rnd = new Random();
                Curve theCrv = obstCrvs[roadRand.Next(borderNum)];

                recursiveLine(theCrv, ref obstCrvs, roadRand, 1, iterationcount);
            }

            model.roadNetwork = obstCrvs;

            if (obstCrvs.Count < borderNum)
                return false;
            else
                return true;
        }

        public bool recursiveLine(Curve inputcurve, ref List<Curve> inputObstacle, Random inputRand, int dir, int count)
        {
            if (count < 1)
                return false;

            Plane perpFrame;
            var t = inputRand.Next(20,80) * 0.01;

            //select random point on one edge
            var pt = inputcurve.PointAtNormalizedLength(t);
            inputcurve.PerpendicularFrameAt(t, out perpFrame);
            var pt2 = Point3d.Add(pt, perpFrame.XAxis * dir);

            var ln = new Line(pt, pt2);
            var lnExt = ln.ToNurbsCurve().ExtendByLine(CurveEnd.End, inputObstacle);

            if (lnExt == null)
                return false;

            inputObstacle.Add(lnExt);
            
            //RhinoDoc.ActiveDoc.Objects.AddPoint(pt);
            //RhinoDoc.ActiveDoc.Objects.AddLine(lnExt.PointAtStart, lnExt.PointAtEnd);
            //RhinoDoc.ActiveDoc.Views.Redraw();

            recursiveLine(lnExt, ref inputObstacle, inputRand,  1, count - 1);
            recursiveLine(lnExt, ref inputObstacle, inputRand, -1, count - 1);

            return true;
        }

        public bool createBlocks(urbanModel model)
        {
            var multiFace = model.precinctSrf.ToBrep().Faces[0].Split(model.roadNetwork, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

            var blocks = new List<Brep>();

            foreach (var faces in multiFace.Faces)
            {
                var face = faces.DuplicateFace(false);
                face.Faces.ShrinkFaces();
                blocks.Add(face);
                RhinoDoc.ActiveDoc.Objects.AddBrep(face);
            }

            if (blocks.Count > 0)
            {
                model.blocks = blocks;
                return true;
            }
            else
            {
                RhinoApp.WriteLine("Blocks failed.");
                return false;
            }
        }

        public bool subdivideBlocks(urbanModel model, double minLength, double minWidth, double maxLength, double maxWidth)
        {
            model.plots = new List<Brep>();

            foreach (var block in model.blocks)
            {
                Point3d subPt1 = new Point3d();
                Point3d subPt2 = new Point3d();
                List<Curve> splitLines = new List<Curve>();
                List<Point3d> evalPoints = new List<Point3d>();

                var obstCrvs = block.DuplicateNakedEdgeCurves(true, false).ToList();

                block.Faces[0].SetDomain(0, new Interval(0, 1));
                block.Faces[0].SetDomain(1, new Interval(0, 1));

                var pt1 = block.Surfaces[0].PointAt(0, 0);
                var pt2 = block.Surfaces[0].PointAt(0, 1);
                var pt3 = block.Surfaces[0].PointAt(1, 1);
                var pt4 = block.Surfaces[0].PointAt(1, 0);

                var length = pt1.DistanceTo(pt2);
                var width  = pt1.DistanceTo(pt4);

                if (length > width)
                {
                    if (width > (minLength * 2))
                    {
                        subPt1 = block.Surfaces[0].PointAt(0.5, 0);
                        subPt2 = block.Surfaces[0].PointAt(0.5, 1);
                    }
                }
                else
                {
                    if (length > (minLength * 2))
                    {
                        subPt1 = block.Surfaces[0].PointAt(0, 0.5);
                        subPt2 = block.Surfaces[0].PointAt(1, 0.5);
                    }
                }

                var subCrv = new Line(subPt1, subPt2).ToNurbsCurve();
                splitLines.Add(subCrv);

                var crvLength = subCrv.GetLength();
                var plotNum = Math.Ceiling(crvLength / maxWidth);

                for (int i = 1; i < plotNum; i++)
                {
                    var val = i * (1 / plotNum);
                    var evalPt = subCrv.PointAtNormalizedLength(val);

                    Plane perpFrame;

                    subCrv.PerpendicularFrameAt(val, out perpFrame);
                    var evalPtUp = Point3d.Add(evalPt,  perpFrame.XAxis);
                    var evalPtDn = Point3d.Add(evalPt, -perpFrame.XAxis);

                    var ln1 = new Line(evalPt, evalPtUp);
                    var ln2 = new Line(evalPt, evalPtDn);

                    var lnExt1 = ln1.ToNurbsCurve().ExtendByLine(CurveEnd.End, obstCrvs);
                    var lnExt2 = ln2.ToNurbsCurve().ExtendByLine(CurveEnd.End, obstCrvs);

                    splitLines.Add(lnExt1);
                    splitLines.Add(lnExt2);
                }

                var plotMultiFace = block.Faces[0].Split(splitLines, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

                foreach (var faces in plotMultiFace.Faces)
                {
                    var face = faces.DuplicateFace(false);
                    face.Faces.ShrinkFaces();
                    model.plots.Add(face);
                    RhinoDoc.ActiveDoc.Objects.AddBrep(face);
                }
            }
            return true;
        }

        public void instantiateBuildings(urbanModel model)
        {

        }
    }
}
