// --------------------------------------------------------------------------------------------------------------------
// <copyright file="TexturedFaceMeshViewer.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace FaceTracking3D
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Media.Media3D;
    using Microsoft.Kinect;
    using Microsoft.Kinect.Toolkit.FaceTracking;
    using System.IO;
    using System.Text;
    using Point = System.Windows.Point;

    /// <summary>
    /// Interaction logic for TexturedFaceMeshViewer.xaml
    /// </summary>
    public partial class TexturedFaceMeshViewer : UserControl, IDisposable
    {
        public static readonly DependencyProperty KinectProperty = DependencyProperty.Register(
            "Kinect",
            typeof(KinectSensor),
            typeof(TexturedFaceMeshViewer),
            new UIPropertyMetadata(
                null,
                (o, args) =>
                ((TexturedFaceMeshViewer)o).OnKinectChanged((KinectSensor)args.OldValue, (KinectSensor)args.NewValue)));

        private static readonly int Bgr32BytesPerPixel = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;

        private byte[] colorImage;

        private ColorImageFormat colorImageFormat = ColorImageFormat.Undefined;

        private WriteableBitmap colorImageWritableBitmap;

        private short[] depthImage;

        private DepthImageFormat depthImageFormat = DepthImageFormat.Undefined;

        private FaceTracker faceTracker;

        private Skeleton[] skeletonData;

        private int trackingId = -1;

        public int m = 0;
        private FaceTriangle[] triangleIndices;

        //public float[,,] data;
        public static float[,,] data=new float[1000,121, 3];
        public int dataindex=0;
        public TexturedFaceMeshViewer()
        {
            this.DataContext = this;
            this.InitializeComponent();
        } 

        public KinectSensor Kinect
        {
            get
            {
                return (KinectSensor)this.GetValue(KinectProperty);
            }

            set
            {
                this.SetValue(KinectProperty, value);
            }
        }

        public void Dispose()
        {
            this.DestroyFaceTracker();
        }

        private void AllFramesReady(object sender, AllFramesReadyEventArgs allFramesReadyEventArgs)
        {
            ColorImageFrame colorImageFrame = null;
            DepthImageFrame depthImageFrame = null;
            SkeletonFrame skeletonFrame = null;

            try
            {
                colorImageFrame = allFramesReadyEventArgs.OpenColorImageFrame();
                depthImageFrame = allFramesReadyEventArgs.OpenDepthImageFrame();
                skeletonFrame = allFramesReadyEventArgs.OpenSkeletonFrame();

                if (colorImageFrame == null || depthImageFrame == null || skeletonFrame == null)
                {
                    return;
                }

                // Check for changes in any of the data this function is receiving
                // and reset things appropriately.
                if (this.depthImageFormat != depthImageFrame.Format)
                {
                    this.DestroyFaceTracker();
                    this.depthImage = null;
                    this.depthImageFormat = depthImageFrame.Format;
                }

                if (this.colorImageFormat != colorImageFrame.Format)
                {
                    this.DestroyFaceTracker();
                    this.colorImage = null;
                    this.colorImageFormat = colorImageFrame.Format;
                    this.colorImageWritableBitmap = null;
                    this.ColorImage.Source = null;
                    this.theMaterial.Brush = null;
                }

                if (this.skeletonData != null && this.skeletonData.Length != skeletonFrame.SkeletonArrayLength)
                {
                    this.skeletonData = null;
                }

                // Create any buffers to store copies of the data we work with
                if (this.depthImage == null)
                {
                    this.depthImage = new short[depthImageFrame.PixelDataLength];
                }

                if (this.colorImage == null)
                {
                    this.colorImage = new byte[colorImageFrame.PixelDataLength];
                }

                if (this.colorImageWritableBitmap == null)
                {
                    this.colorImageWritableBitmap = new WriteableBitmap(
                        colorImageFrame.Width, colorImageFrame.Height, 96, 96, PixelFormats.Bgr32, null);
                    this.ColorImage.Source = this.colorImageWritableBitmap;
                    this.theMaterial.Brush = new ImageBrush(this.colorImageWritableBitmap)
                        {
                            ViewportUnits = BrushMappingMode.Absolute
                        };
                }

                if (this.skeletonData == null)
                {
                    this.skeletonData = new Skeleton[skeletonFrame.SkeletonArrayLength];
                }

                // Copy data received in this event to our buffers.
                colorImageFrame.CopyPixelDataTo(this.colorImage);
                depthImageFrame.CopyPixelDataTo(this.depthImage);
                skeletonFrame.CopySkeletonDataTo(this.skeletonData);
                this.colorImageWritableBitmap.WritePixels(
                    new Int32Rect(0, 0, colorImageFrame.Width, colorImageFrame.Height),
                    this.colorImage,
                    colorImageFrame.Width * Bgr32BytesPerPixel,
                    0);

                // Find a skeleton to track.
                // First see if our old one is good.
                // When a skeleton is in PositionOnly tracking state, don't pick a new one
                // as it may become fully tracked again.
                Skeleton skeletonOfInterest =
                    this.skeletonData.FirstOrDefault(
                        skeleton =>
                        skeleton.TrackingId == this.trackingId
                        && skeleton.TrackingState != SkeletonTrackingState.NotTracked);

                if (skeletonOfInterest == null)
                {
                    // Old one wasn't around.  Find any skeleton that is being tracked and use it.
                    skeletonOfInterest =
                        this.skeletonData.FirstOrDefault(
                            skeleton => skeleton.TrackingState == SkeletonTrackingState.Tracked);

                    if (skeletonOfInterest != null)
                    {
                        // This may be a different person so reset the tracker which
                        // could have tuned itself to the previous person.
                        if (this.faceTracker != null)
                        {
                            this.faceTracker.ResetTracking();
                        }

                        this.trackingId = skeletonOfInterest.TrackingId;
                    }
                }

                bool displayFaceMesh = false;

                if (skeletonOfInterest != null && skeletonOfInterest.TrackingState == SkeletonTrackingState.Tracked)
                {
                    if (this.faceTracker == null)
                    {
                        try
                        {
                            this.faceTracker = new FaceTracker(this.Kinect);
                        }
                        catch (InvalidOperationException)
                        {
                            // During some shutdown scenarios the FaceTracker
                            // is unable to be instantiated.  Catch that exception
                            // and don't track a face.
                            Debug.WriteLine("AllFramesReady - creating a new FaceTracker threw an InvalidOperationException");
                            this.faceTracker = null;
                        }
                    }

                    if (this.faceTracker != null)
                    {
                        FaceTrackFrame faceTrackFrame = this.faceTracker.Track(
                            this.colorImageFormat,
                            this.colorImage,
                            this.depthImageFormat,
                            this.depthImage,
                            skeletonOfInterest);

                        if (faceTrackFrame.TrackSuccessful && button1.Content.ToString()=="Collecting")
                        {
                            this.UpdateMesh(faceTrackFrame);

                            // Only display the face mesh if there was a successful track.
                            displayFaceMesh = true;
                        }
                    }
                }
                else
                {
                    this.trackingId = -1;
                }

                this.viewport3d.Visibility = displayFaceMesh ? Visibility.Visible : Visibility.Hidden;
            }
            finally
            {
                if (colorImageFrame != null)
                {
                    colorImageFrame.Dispose();
                }

                if (depthImageFrame != null)
                {
                    depthImageFrame.Dispose();
                }

                if (skeletonFrame != null)
                {
                    skeletonFrame.Dispose();
                }
            }
        }

        private void DestroyFaceTracker()
        {
            if (this.faceTracker != null)
            {
                this.faceTracker.Dispose();
                this.faceTracker = null;
            }
        }

        private void OnKinectChanged(KinectSensor oldSensor, KinectSensor newSensor)
        {
            if (oldSensor != null)
            {
                try
                {
                    oldSensor.AllFramesReady -= this.AllFramesReady;

                    this.DestroyFaceTracker();
                }
                catch (InvalidOperationException)
                {
                    // KinectSensor might enter an invalid state while enabling/disabling streams or stream features.
                    // E.g.: sensor might be abruptly unplugged.
                }
            }

            if (newSensor != null)
            {
                try
                {
                    this.faceTracker = new FaceTracker(this.Kinect);

                    newSensor.AllFramesReady += this.AllFramesReady;
                }
                catch (InvalidOperationException)
                {
                    // KinectSensor might enter an invalid state while enabling/disabling streams or stream features.
                    // E.g.: sensor might be abruptly unplugged.
                }
            }
        }
        public float GetVectorDegree(float ax, float ay, float az, float bx, float by, float bz)
        {
            float cosDegree = 0;
            cosDegree = Math.Abs(ax * bx + ay * by + az * bz) / (((float)Math.Sqrt(ax * ax + ay * ay + az * az) * (float)Math.Sqrt(bx * bx + by * by + bz * bz)));
            return (cosDegree);
        }
        void UpdateMesh(FaceTrackFrame faceTrackingFrame)
        {
            EnumIndexableCollection<FeaturePoint, Vector3DF> shapePoints = faceTrackingFrame.Get3DShape();
            EnumIndexableCollection<FeaturePoint, PointF> projectedShapePoints = faceTrackingFrame.GetProjected3DShape();

            if (this.triangleIndices == null)
            {
                // Update stuff that doesn't change from frame to frame
                this.triangleIndices = faceTrackingFrame.GetTriangles();
                var indices = new Int32Collection(this.triangleIndices.Length * 3);
                foreach (FaceTriangle triangle in this.triangleIndices)
                {
                    indices.Add(triangle.Third);
                    indices.Add(triangle.Second);
                    indices.Add(triangle.First);
                }

                this.theGeometry.TriangleIndices = indices;
                this.theGeometry.Normals = null; // Let WPF3D calculate these.

                this.theGeometry.Positions = new Point3DCollection(shapePoints.Count);
                this.theGeometry.TextureCoordinates = new PointCollection(projectedShapePoints.Count);
                for (int pointIndex = 0; pointIndex < shapePoints.Count; pointIndex++)
                {
                    this.theGeometry.Positions.Add(new Point3D());
                    this.theGeometry.TextureCoordinates.Add(new Point());
                }
            }
            //Vector3DF point=shapePoints[3];
            // Update the 3D model's vertices and texture coordinates
            
            for (int pointIndex = 0; pointIndex < shapePoints.Count; pointIndex++)
            {
                Vector3DF point = shapePoints[pointIndex];
                this.theGeometry.Positions[pointIndex] = new Point3D(point.X, point.Y, -point.Z);

                PointF projected = projectedShapePoints[pointIndex];
                data[dataindex,pointIndex, 0] = point.X;
                data[dataindex,pointIndex, 1] = point.Y;
                data[dataindex,pointIndex, 2] = point.Z;
                this.theGeometry.TextureCoordinates[pointIndex] =
                    new Point(
                        projected.X / (double)this.colorImageWritableBitmap.PixelWidth,
                        projected.Y / (double)this.colorImageWritableBitmap.PixelHeight);
            }
            if (data[dataindex, 4, 2] > 1)
            {
                dataindex++;
                //StreamWriter fw = File.AppendText("e:\\newnewstart4.txt");
                //fw.Write("z=" + );
                //fw.Close();
            }
        }

        private void button1_Click(object sender, RoutedEventArgs e)
        {
            if (button1.Content.ToString() == "START COLLECTION")
            {
                button1.Content = "Collecting";
                textBox1.Text = "0";
                textBox2.Text = "0";
                textBox3.Text = "0";
                textBox4.Text = "0";
                textBox5.Text = "0";
                textBox6.Text = "0";
                textBox7.Text = "0";
                textBox8.Text = "0";
                textBox9.Text = "0";
                textBox10.Text = "0";
                textBox11.Text = "0";
                textBox12.Text = "0";
                textBox13.Text = "0";
                textBox14.Text = "0";
                textBox15.Text = "0";
                textBox16.Text = "0";
                textBox18.Text = "0";
            }
            else if (button1.Content.ToString() == "Collecting")
            {
                button1.Content = "Collected";
            }
            else if (button1.Content.ToString() == "Collected")
            {
                button1.Content = "START COLLECTION";
                dataindex = 0;
            }

        }

        private void button2_Click(object sender, RoutedEventArgs e)
        {
            if (button1.Content.ToString() == "Collected")
            {
                float HeadHeight = 0;
                float NoseHeight = 0;
                float LeftEyeWidth = 0;
                float RightEyeHeight = 0;
                float RightEyeWidth = 0;
                float LeftEyeHeight = 0;
                float MouthWidth = 0;
                float HeadWidth = 0;
                float ChinWidth = 0;
                float OutEyeWidth = 0;
                float LeftCheekMouthLength = 0;
                float RightCheekMouthLength = 0;
                float ChinHeight = 0;
                float InEyeWidth = 0;
                float GoldenTriangleDegree = 0;
                float SilverTriangleDegree = 0;
                float BronzeTriangleDegree = 0;

                dataindex--;

                for (int i = 0; i < this.dataindex; i++)
                {
                    HeadHeight += (float)Math.Pow((data[i, 0, 0] - data[i, 10, 0]) * (data[i, 0, 0] - data[i, 10, 0]) + (data[i, 0, 1] - data[i, 10, 1]) * (data[i, 0, 1] - data[i, 10, 1]) + Math.Pow(data[i, 0, 2] - data[i, 10, 2], 2), 0.5);
                    NoseHeight += (float)Math.Pow((data[i, 94, 0] - data[i, 39, 0]) * (data[i, 94, 0] - data[i, 39, 0]) + (data[i, 94, 1] - data[i, 39, 1]) * (data[i, 94, 1] - data[i, 39, 1]) + Math.Pow(data[i, 94, 2] - data[i, 39, 2], 2), 0.5);
                    LeftEyeWidth += (float)Math.Pow((data[i, 53, 0] - data[i, 56, 0]) * (data[i, 53, 0] - data[i, 56, 0]) + (data[i, 53, 1] - data[i, 56, 1]) * (data[i, 53, 1] - data[i, 56, 1]) + Math.Pow(data[i, 53, 2] - data[i, 56, 2], 2), 0.5);
                    RightEyeHeight += (float)Math.Pow(Math.Pow(data[i, 22, 0] - data[i, 21, 0], 2) + Math.Pow(data[i, 22, 1] - data[i, 21, 1], 2) + Math.Pow(data[i, 22, 2] - data[i, 21, 2], 2), 0.5);
                    RightEyeWidth += (float)Math.Pow(Math.Pow(data[i, 20, 0] - data[i, 23, 0], 2) + Math.Pow(data[i, 23, 1] - data[i, 20, 1], 2) + Math.Pow(data[i, 23, 2] - data[i, 20, 2], 2), 0.5);
                    LeftEyeHeight += (float)Math.Pow(Math.Pow(data[i, 54, 0] - data[i, 55, 0], 2) + Math.Pow(data[i, 54, 1] - data[i, 55, 1], 2) + Math.Pow(data[i, 54, 2] - data[i, 55, 2], 2), 0.5);
                    MouthWidth += (float)Math.Pow(Math.Pow(data[i, 88, 0] - data[i, 89, 0], 2) + Math.Pow(data[i, 88, 1] - data[i, 89, 1], 2) + Math.Pow(data[i, 88, 2] - data[i, 89, 2], 2), 0.5);
                    HeadWidth += (float)Math.Pow(Math.Pow(data[i, 117, 0] - data[i, 113, 0], 2) + Math.Pow(data[i, 117, 1] - data[i, 113, 1] + Math.Pow(data[i, 117, 2] - data[i, 113, 2], 2), 2), 0.5);
                    ChinWidth += (float)Math.Pow(Math.Pow(data[i, 30, 0] - data[i, 63, 0], 2) + Math.Pow(data[i, 30, 1] - data[i, 63, 1], 2) + Math.Pow(data[i, 30, 2] - data[i, 63, 2], 2), 0.5);
                    OutEyeWidth += (float)Math.Pow(Math.Pow(data[i, 20, 0] - data[i, 53, 0], 2) + Math.Pow(data[i, 20, 1] - data[i, 53, 1], 2) + Math.Pow(data[i, 20, 2] - data[i, 53, 2], 2), 0.5);
                    LeftCheekMouthLength += (float)Math.Pow(Math.Pow(data[i, 88, 0] - data[i, 91, 0], 2) + Math.Pow(data[i, 88, 1] - data[i, 91, 1], 2) + Math.Pow(data[i, 88, 2] - data[i, 91, 2], 2), 0.5);
                    RightCheekMouthLength += (float)Math.Pow(Math.Pow(data[i, 89, 0] - data[i, 90, 0], 2) + Math.Pow(data[i, 89, 1] - data[i, 90, 1], 2) + Math.Pow(data[i, 89, 2] - data[i, 90, 2], 2), 0.5);
                    ChinHeight += (float)Math.Pow(Math.Pow(data[i, 9, 0] - data[i, 10, 0], 2) + Math.Pow(data[i, 9, 1] - data[i, 10, 1], 2) + Math.Pow(data[i, 9, 2] - data[i, 10, 2], 2), 0.5);
                    InEyeWidth += (float)Math.Pow(Math.Pow(data[i, 23, 0] - data[i, 56, 0], 2) + Math.Pow(data[i, 23, 1] - data[i, 56, 1], 2) + Math.Pow(data[i, 23, 2] - data[i, 56, 2], 2), 0.5);
                    GoldenTriangleDegree += GetVectorDegree(data[i, 26, 0] - data[i, 39, 0], data[i, 26, 1] - data[i, 39, 1], data[i, 26, 2] - data[i, 39, 2], data[i, 59, 0] - data[i, 39, 0], data[i, 59, 1] - data[i, 39, 1], data[i, 59, 2] - data[i, 39, 2]);
                    SilverTriangleDegree += GetVectorDegree(data[i, 20, 0] - data[i, 23, 0], data[i, 20, 1] - data[i, 23, 1], data[i, 20, 2] - data[i, 23, 2], data[i, 53, 0] - data[i, 56, 0], data[i, 53, 1] - data[i, 56, 1], data[i, 53, 2] - data[i, 56, 2]);
                    BronzeTriangleDegree += GetVectorDegree(data[i, 10, 0] - data[i, 32, 0], data[i, 10, 1] - data[i, 32, 1], data[i, 10, 2] - data[i, 32, 2], data[i, 65, 0] - data[i, 10, 0], data[i, 65, 1] - data[i, 10, 1], data[i, 65, 2] - data[i, 10, 2]);
                    //string fileName = "e:\\rawdata" + m +".txt";
                    //StreamWriter uw = File.AppendText(fileName);
                    //uw.Write(
                    //    "dataindex=" + dataindex + ";" +
                    //    "HeadHeight:" +
                    //         HeadHeight + "                            " +
                    //    "HeadWidth:" +
                    //         HeadWidth + "                            " +
                    //    "InEyeWidth:" +
                    //         InEyeWidth + "                            " +
                    //         "OutEyeWidth:" +
                    //         OutEyeWidth + "                            " +
                    //    "NoseHeight:" +
                    //         NoseHeight + "                            " +
                    //    "LeftEyeWidth:" +
                    //         LeftEyeWidth + "                            " +
                    //    "LeftEyeHeight:" +
                    //         LeftEyeHeight + "                            " +
                    //    "RightEyeWidth:" +
                    //         RightEyeWidth + "                            " +
                    //    "RightEyeHeight:" +
                    //         RightEyeHeight + "                            " +
                    //    "LeftCheekMouthLength:" +
                    //         LeftCheekMouthLength + "                            " +
                    //         "RightCheekMouthLength:" +
                    //         RightCheekMouthLength + "                            " +
                    //         "ChinHeight:" +
                    //         ChinHeight + "                            " +
                    //    "ChinWidth:" +
                    //         ChinWidth + "                            " +
                    //         "GoldenTriangleDegree:" +
                    //         GoldenTriangleDegree + "                          " +
                    //         "SilverTriangleDegree:" +
                    //         SilverTriangleDegree + "                            ");
                    //uw.Close();
                    //StreamWriter fw = File.AppendText(fileName);
                    //fw.Write("and & and");
                    //fw.Close();
                    
                }
                textBox1.Text = (HeadHeight / (float)this.dataindex).ToString();
                textBox2.Text = (NoseHeight / (float)this.dataindex).ToString();
                textBox3.Text = (LeftEyeWidth / (float)this.dataindex).ToString();
                textBox4.Text = (RightEyeHeight / (float)this.dataindex).ToString();
                textBox5.Text = (RightEyeWidth / (float)this.dataindex).ToString();
                textBox6.Text = (LeftEyeHeight / (float)this.dataindex).ToString();
                textBox7.Text = (MouthWidth / (float)this.dataindex).ToString();
                textBox8.Text = (HeadWidth / (float)this.dataindex).ToString();
                textBox9.Text = (ChinWidth / (float)this.dataindex).ToString();
                textBox10.Text = (OutEyeWidth / (float)this.dataindex).ToString();
                textBox11.Text = (LeftCheekMouthLength / (float)this.dataindex).ToString();
                textBox12.Text = (RightCheekMouthLength / (float)this.dataindex).ToString();
                textBox13.Text = (ChinHeight / (float)this.dataindex).ToString();
                textBox14.Text = (InEyeWidth / (float)this.dataindex).ToString();
                textBox15.Text = (GoldenTriangleDegree / (float)this.dataindex).ToString();
                textBox16.Text = (SilverTriangleDegree / (float)this.dataindex).ToString();
                textBox18.Text = (BronzeTriangleDegree / (float)this.dataindex).ToString();
                textBox17.Visibility = Visibility.Visible;
                textBlock17.Visibility = Visibility.Visible;
                button3.Visibility = Visibility.Visible;
            }
            button2.Content = "Calculated";
        }

        private void button3_Click(object sender, RoutedEventArgs e)
        {
            string dataName = textBox17.Text.ToString();
            string fileName = "e:\\Faceof"+dataName+".txt";
            StreamWriter uw = File.AppendText(fileName);   
            uw.WriteLine("Name:{0}",dataName);
            uw.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16}\n",textBox1.Text.ToString(),
                textBox2.Text.ToString(), textBox3.Text.ToString() ,textBox4.Text.ToString() ,textBox5.Text.ToString(),textBox6.Text.ToString() ,textBox7.Text.ToString() ,
                textBox8.Text.ToString() ,textBox9.Text.ToString() ,textBox10.Text.ToString(), textBox11.Text.ToString() ,textBox12.Text.ToString(), textBox13.Text.ToString() ,
                textBox14.Text.ToString() , textBox15.Text.ToString(), textBox16.Text.ToString(),textBox18.Text.ToString());
            uw.Close();
            MessageBox.Show("Successfully saved!","Button Message");
            button1.Content = "START COLLECTION";
            button2.Content = "Calculation";
            textBox17.Visibility = Visibility.Hidden;
            textBlock17.Visibility = Visibility.Hidden;
            button3.Visibility = Visibility.Hidden;
            dataindex = 0;
            m++;
        }

        private void button4_Click(object sender, RoutedEventArgs e)
        {
            button1.Content = "START COLLECTION";
            button2.Content = "Calculation";
            dataindex = 0;
            textBox17.Visibility = Visibility.Hidden;
            textBlock17.Visibility = Visibility.Hidden;
            button3.Visibility = Visibility.Hidden;
            textBox1.Text = "";
            textBox2.Text = "";
            textBox3.Text = "";
            textBox4.Text = "";
            textBox5.Text = "";
            textBox6.Text = "";
            textBox7.Text = "";
            textBox8.Text = "";
            textBox9.Text = "";
            textBox10.Text = "";
            textBox11.Text = "";
            textBox12.Text = "";
            textBox13.Text = "";
            textBox14.Text = "";
            textBox15.Text = "";
            textBox16.Text = "";
            textBox18.Text = ""; 
            Array.Clear(data, 0, data.Length);
        }

       

    }
}