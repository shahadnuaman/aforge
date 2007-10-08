// AForge Vision Library
// AForge.NET framework
//
// Copyright � Andrew Kirillov, 2007
// andrew.kirillov@gmail.com
//
namespace AForge.Vision
{
    using System;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Runtime.InteropServices;
    using System.Threading;

    using AForge.Imaging;

    /// <summary>
    /// Motion detector based on background modeling (high precision version) with
    /// ability to count objects using blob counting algorithm.
    /// </summary>
    /// 
    /// <remarks><para></para></remarks>
    /// 
    public class CountingMotionDetector : IMotionDetector
    {
        // frame's dimension
        private int width;
        private int height;
        private int frameSize;

        // background frame of video stream
        private IntPtr backgroundFrame;
        // current frame of video sream
        private IntPtr currentFrame;
        // pixel changed in the new frame of video stream
        private int pixelsChanged;

        // the flag determines if it is required to highlight motion regions
        private bool highlightMotionRegions = false;

        // threshold values
        private int differenceThreshold = 15;
        private int differenceThresholdNeg = -15;

        private int framesPerBackgroundUpdate = 2;
        private int framesCounter = 0;

        // blob's counter algorithm
        private BlobCounter blobCounter = new BlobCounter( );

        /// <summary>
        /// Highlight motion regions or not.
        /// </summary>
        /// 
        /// <remarks><para>Turning the value on leads to more processing time of video frame. Default values
        /// is <b>false</b>.</para>
        /// <note>Each moving object is highlighted by rectangle, but not its contour.</note>
        /// </remarks>
        /// 
        public bool HighlightMotionRegions
        {
            get { return highlightMotionRegions; }
            set { highlightMotionRegions = value; }
        }

        /// <summary>
        /// Highligh motion edges or not (not supported).
        /// </summary>
        /// 
        /// <remarks><note>The property always returns <b>true</b>. Setting this property
        /// does not have any effect. If <see cref="HighlightMotionRegions"/> property is
        /// set to <b>true</b>, then moving objects are highlighted by rectangles.</note>
        /// </remarks>
        /// 
        public bool HighlightMotionEdges
        {
            get { return true; }
            set { }
        }

        /// <summary>
        /// Difference threshold value.
        /// </summary>
        /// 
        /// <remarks>The value specifies the amount off difference between pixels, which is treated
        /// as motion pixel. Default value is <b>15</b>.</remarks>
        /// 
        public int DifferenceThreshold
        {
            get { return differenceThreshold; }
            set
            {
                differenceThreshold = Math.Max( 1, Math.Min( 255, value ) );
                differenceThresholdNeg = -differenceThreshold;
            }
        }

        /// <summary>
        /// Frames per background update.
        /// </summary>
        /// 
        /// <remarks><para>The value controls the speed of background adaptation to scene changes. After
        /// each specified amount of frames the background frame is updated in the direction
        /// to decrease difference with current processing frame.</para>
        /// <para>The default value is 2. The value may be set in the range of [1, 50].</para>
        /// </remarks>
        /// 
        public int FramesPerBackgroundUpdate
        {
            get { return framesPerBackgroundUpdate; }
            set { framesPerBackgroundUpdate = Math.Max( 1, Math.Min( 50, value ) ); }
        }

        /// <summary>
        /// Motion level value.
        /// </summary>
        /// 
        /// <remarks><para>Amount of changes in the last processed frame in percents.</para>
        /// <note>This property is based on amount of changed pixels, what it includes noise
        /// and also objects of all size. For motion alarm the <see cref="ObjectCount"/>
        /// property can be used as alternative.</note>
        /// </remarks>
        /// 
        public double MotionLevel
        {
            get { return (double) pixelsChanged / ( width * height ); }
        }

        /// <summary>
        /// Specifies minimum width of acceptable object.
        /// </summary>
        /// 
        public int MinObjectsWidth
        {
            get { return blobCounter.MinWidth; }
            set
            {
                lock ( blobCounter )
                {
                    blobCounter.MinWidth = value;
                }
            }
        }

        /// <summary>
        /// Specifies minimum height of acceptable object.
        /// </summary>
        /// 
        public int MinObjectsHeight
        {
            get { return blobCounter.MinHeight; }
            set
            {
                lock ( blobCounter )
                {
                    blobCounter.MinHeight = value;
                }
            }
        }

        /// <summary>
        /// Specifies maximum width of acceptable object.
        /// </summary>
        /// 
        public int MaxObjectsWidth
        {
            get { return blobCounter.MaxWidth; }
            set
            {
                lock ( blobCounter )
                {
                    blobCounter.MaxWidth = value;
                }
            }
        }

        /// <summary>
        /// Specifies maximum height of acceptable object.
        /// </summary>
        /// 
        public int MaxObjectsHeight
        {
            get { return blobCounter.MaxHeight; }
            set
            {
                lock ( blobCounter )
                {
                    blobCounter.MaxHeight = value;
                }
            }
        }

        /// <summary>
        /// Amount of objects found.
        /// </summary>
        /// 
        public int ObjectCount
        {
            get
            {
                lock ( blobCounter )
                {
                    return blobCounter.ObjectsCount;
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CountingMotionDetector"/> class.
        /// </summary>
        /// 
        public CountingMotionDetector( ) : this( false ) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="CountingMotionDetector"/> class.
        /// </summary>
        /// 
        /// <param name="highlightMotionRegions">Highlight motion regions or not.</param>
        /// 
        public CountingMotionDetector( bool highlightMotionRegions )
        {
            this.highlightMotionRegions = highlightMotionRegions;

            // blob detection settings
            blobCounter.FilterBlobs = true;

            blobCounter.MinWidth  = 10;
            blobCounter.MinHeight = 10;
        }

        /// <summary>
        /// Process new frame.
        /// </summary>
        /// 
        /// <remarks>Process new frame of video source and detect motion.</remarks>
        /// 
        public unsafe void ProcessFrame( Bitmap image )
        {
            BitmapData imageData = null;

            // check previous frame
            if ( backgroundFrame == IntPtr.Zero )
            {
                // save image dimension
                width = image.Width;
                height = image.Height;
                frameSize = width * height;

                // alocate memory for previous and current frames
                backgroundFrame = Marshal.AllocHGlobal( frameSize );
                currentFrame = Marshal.AllocHGlobal( frameSize );

                // lock source image
                imageData = image.LockBits(
                    new Rectangle( 0, 0, width, height ),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb );

                // convert source frame to grayscale
                ImageProcessingTools.GrayscaleImage( imageData, backgroundFrame );

                // unlock source image
                image.UnlockBits( imageData );

                return;
            }

            // check image dimension
            if ( ( image.Width != width ) || ( image.Height != height ) )
                return;

            // lock source image
            imageData = image.LockBits(
                new Rectangle( 0, 0, width, height ),
                ( highlightMotionRegions ) ? ImageLockMode.ReadWrite : ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb );

            // convert current image to grayscale
            ImageProcessingTools.GrayscaleImage( imageData, currentFrame );

            // pointers to background and current frames
            byte* backFrame;
            byte* currFrame;
            int diff;

            // update background frame
            if ( ++framesCounter == framesPerBackgroundUpdate )
            {
                framesCounter = 0;

                backFrame = (byte*) backgroundFrame.ToPointer( );
                currFrame = (byte*) currentFrame.ToPointer( );

                for ( int i = 0; i < frameSize; i++, backFrame++, currFrame++ )
                {
                    diff = *currFrame - *backFrame;
                    if ( diff > 0 )
                    {
                        ( *backFrame )++;
                    }
                    else if ( diff < 0 )
                    {
                        ( *backFrame )--;
                    }
                }
            }

            backFrame = (byte*) backgroundFrame.ToPointer( );
            currFrame = (byte*) currentFrame.ToPointer( );

            // 1 - get difference between frames
            // 2 - threshold the difference
            // 3 - calculate amount of motion pixels
            for ( int i = 0; i < frameSize; i++, backFrame++, currFrame++ )
            {
                // difference
                diff = (int) *currFrame - (int) *backFrame;
                // treshold
                *currFrame = ( ( diff >= differenceThreshold ) || ( diff <= differenceThresholdNeg ) ) ? (byte) 1 : (byte) 0;
                // amount of motion
                pixelsChanged += *currFrame;
            }

            // count objects
            lock ( blobCounter )
            {
                blobCounter.ProcessImage( currentFrame, width, height, width );
            }

            if ( highlightMotionRegions )
            {
                // highlight each moving object
                Rectangle[] rects = blobCounter.GetObjectRectangles( );

                foreach ( Rectangle rect in rects )
                {
                    Drawing.Rectangle( imageData, rect, Color.Red );
                }
            }

            // unlock source image
            image.UnlockBits( imageData );
        }

        /// <summary>
        /// Reset detector to initial state.
        /// </summary>
        /// 
        /// <remarks>Resets internal state and variables of motion detection algorithm.</remarks>
        /// 
        public void Reset( )
        {
            if ( backgroundFrame != IntPtr.Zero )
            {
                Marshal.FreeHGlobal( backgroundFrame );
                backgroundFrame = IntPtr.Zero;
            }

            if ( currentFrame != IntPtr.Zero )
            {
                Marshal.FreeHGlobal( currentFrame );
                currentFrame = IntPtr.Zero;
            }

            framesCounter = 0;
        }
    }
}