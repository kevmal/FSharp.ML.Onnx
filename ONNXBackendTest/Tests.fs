namespace Test
open Microsoft.ML.OnnxRuntime.Tensors
open NUnit.Framework
open Onnx
open ProtoBuf
open System
open System.IO
open Microsoft.ML.OnnxRuntime
open Microsoft.FSharp.Quotations
open Common
open ExprGraph

type on = ONNXAPI.ONNX

[<AutoOpen>]
module X = 
    type ONNXAPI.ONNX with
        [<ReflectedDefinition>]
        static member reshape(x: Tensor<float32>,shape: int32[]) = on.reshape(x,(shape |> Array.map int64).ToTensor())

module MiniGraphs = 
    let input1 = ArrayTensorExtensions.ToTensor(Array2D.create 1 32 2.f) :> Tensor<float32>
    let input2 = ArrayTensorExtensions.ToTensor(Array2D.create 32 1 3.f) :> Tensor<float32>

    let input1Int = ArrayTensorExtensions.ToTensor(Array2D.create 1 32 2L) :> Tensor<int64>
    let input2Int = ArrayTensorExtensions.ToTensor(Array2D.create 32 1 3L) :> Tensor<int64>

    let input4D1 = ArrayTensorExtensions.ToTensor(Array4D.create 3 3 1 3 2.f) :> Tensor<float32>
    let input4D2 = ArrayTensorExtensions.ToTensor(Array4D.create 3 3 3 1 1.f) :> Tensor<float32>


    [<Test>]
    let ``add float``() = 
        let res1 = on.add(input1,input2)
        let res2 = on.add(input1Int,input2Int)
        if res1.Dimensions.ToArray() <> [|32;32|] then failwith "Incorrect dimmesions"
        if res1 |> Seq.exists (fun x -> x <> 5.f) then failwith "An incorrect value"
        if res2.Dimensions.ToArray() <> [|32;32|] then failwith "Incorrect dimmesions"
        if res2 |> Seq.exists (fun x -> x <> 5L) then failwith "An incorrect value"

    [<Test>]
    let relu() = 
        let xx = Array2D.create 2 2 0.f
        xx.[0,0] <- -1.0f
        xx.[1,1] <- 1.0f
        let res = on.relu(ArrayTensorExtensions.ToTensor(xx) :> Tensor<float32>)
        Assert.AreEqual(0.0, float res.[0,0], 0.001)
        Assert.AreEqual(1.0, float res.[1,1], 0.001)

    [<Test>]
    let convolution() = 
        let img = ArrayTensorExtensions.ToTensor(Array4D.create 1 1 32 32 1.f) :> Tensor<float32>
        let kernel = ArrayTensorExtensions.ToTensor(Array4D.create 8 1 5 5 1.f) :> Tensor<float32>
        let convRes = on.conv(img, kernel, auto_pad= "SAME_UPPER")
        Assert.AreEqual(9.0, float convRes.[0,0,0,0], 0.001)
        Assert.AreEqual(25.0, float convRes.[0,0,5,5], 0.001)

    [<Test>]
    let ``matmul broadcast``() = 
        let res1 = on.mat_mul(input1,input2)
        Assert.AreEqual([|1;1|], res1.shape)
        Assert.AreEqual(192.0f, res1.[0])
        let res2 = on.mat_mul(input2,input1)
        Assert.AreEqual([|32;32|], res2.shape)
        Assert.AreEqual(6.0f, res2.[0])

    [<Test>]
    let ``matmul batch``() = 
        let res1 = on.mat_mul(input4D1,input4D2)
        Assert.AreEqual([|3;3;1;1|], res1.Dimensions.ToArray())
        Assert.AreEqual(6.0f, res1.[0])

    [<Test>]
    let ``eager api``() =
        let input1 = ArrayTensorExtensions.ToTensor(Array2D.create 10000 40 -2.f) :> Tensor<float32>
        let input2 = ArrayTensorExtensions.ToTensor(Array2D.create 40 10000 -2.f) :> Tensor<float32>
        let res = on.mat_mul(input2,on.abs(input1))
        Assert.AreEqual([|40;40|], res.shape)
        Assert.AreEqual(-40000., float res.[0,0], 0.00001)
        

module FullModel = 

    let shouldEqual (msg: string) (v1: 'T) (v2: 'T) = 
        if v1 <> v2 then 
            Assert.Fail(sprintf "fail %s: expected %A, got %A" msg v1 v2)

    let mnistDir = Path.Combine(__SOURCE_DIRECTORY__,"..","data","mnist")

    let test_data = 
            let f(path: string) = 
                TensorProto.Parser.ParseFrom(File.ReadAllBytes(path))
            [| for i in [0;1;2] ->
                    Path.Combine(mnistDir,sprintf "test_data_set_0") 
                    |> fun dir -> (f(Path.Combine(dir,"input_0.pb")),f(Path.Combine(dir,"output_0.pb")))|]

    let testModel(model : byte[]) = 
        use sess = new InferenceSession(model)
        for (index,(input,output)) in test_data |> Array.indexed do
            use values2 = sess.Run([|NamedOnnxValue.CreateFromTensor("Input3",Tensor.FromTensorProtoFloat32(input))|])
            let diff = 
                (values2 |> Seq.toArray |> Array.head |> fun v -> v.AsTensor<float32>() |> Seq.toArray, Tensor.FromTensorProtoFloat32(output) |> Seq.toArray)
                ||> Array.zip
                |> Array.sumBy (fun (x,y) -> System.Math.Abs(x-y))
            if diff > 0.1f then failwithf "Unexpected result in example %i with a difference of %f" index diff

    let testModel2(f: Tensor<float32> -> DV<Tensor<float32>>) = 
        let test_data = 
            let f(path: string) = 
                TensorProto.Parser.ParseFrom(File.ReadAllBytes(path))
            [| for i in [0;1;2] ->
                    Path.Combine(mnistDir,sprintf "test_data_set_%i" i) 
                    |> fun dir -> (f(Path.Combine(dir,"input_0.pb")),f(Path.Combine(dir,"output_0.pb")))|]
        for (index,(input,output)) in test_data |> Array.indexed do
            use values2 = f(Tensor.FromTensorProtoFloat32(input)) 
            let ys = values2.F |> Seq.toArray
            let diff = 
                (ys, Tensor.FromTensorProtoFloat32(output) |> Seq.toArray)
                ||> Array.zip
                |> Array.sumBy (fun (x,y) -> System.Math.Abs(x-y))
            if diff > 0.1f then failwithf "Unexpected result in example %i with a difference of %f" index diff
            printfn "%f %A" diff ys


    [<Test>]
    let ``prebuilt model``() = 
        let model = File.ReadAllBytes(Path.Combine(mnistDir, "model.onnx")) 
        model |> testModel

    [<AutoOpen>]
    module Node = 

        let unaryOp op (attrs: AttributeProto[]) (name: string, input: string, output: string)  =
            simple op (name, [|input|],[|output|],attrs)

        let binaryOp op (attrs: AttributeProto[]) (name: string, left: string, right: string, output: string)  =
            simple op (name, [|left;right|],[|output|],attrs)

        let reshape = binaryOp "Reshape" [||]
        let add = binaryOp "Add" [||]

        let cnn(name: string, 
                input: string, 
                kernel: string, 
                output: string, 
                kernel_shape: int64[], 
                strides: int64[], 
                auto_pad: string , 
                group: int64,
                dilations: int64[]) = 

            let attrs = 
                [|
                    Attr.ints("kernel_shape", kernel_shape)// [|5L;5L|]
                    Attr.ints("strides", strides) // [|1L;1L|]
                    Attr.string("auto_pad", auto_pad) //"SAME_UPPER"
                    Attr.int("group",group) // 1L
                    Attr.ints("dilations",dilations) //[|1L;1L|]
                |] |> Array.choose id

            let np = simple "Conv" (name, [|input;kernel|],[|output|],attrs)
            np

        let pool opType
                   (name: string, 
                    input: string, 
                    output: string, 
                    kernel_shape: int64[], 
                    strides: int64[], 
                    pads: int64[], 
                    auto_pad : string) = 

            let attrs = 
                [|
                    Attr.ints("kernel_shape",kernel_shape)
                    Attr.ints("strides",strides)
                    Attr.ints("pads",pads)
                    Attr.string("auto_pad",auto_pad)
                |] |> Array.choose id
            let np = simple opType (name, [|input|],[|output|],attrs)
            np

        let maxPool = pool "MaxPool"
        let relu(name: string, input: string, output: string) = unaryOp "Relu" [||] (name, input,output)
        let matmul = binaryOp "MatMul"  [||]

    /// This is a full MNist example that exactly matches the pre-trained model
    [<Test>]
    let ``manual model``() =
        let nodes = 
            [|
                reshape ("Times212_reshape1","Parameter193", "Parameter193_reshape1_shape","Parameter193_reshape1")
                cnn("Convolution28","Input3","Parameter5","Convolution28_Output_0",[|5L;5L|],[|1L;1L|],"SAME_UPPER",1L,[|1L;1L|])
                add ("Plus30", "Convolution28_Output_0", "Parameter6","Plus30_Output_0")
                relu("ReLU32","Plus30_Output_0","ReLU32_Output_0")
                maxPool("Pooling66","ReLU32_Output_0", "Pooling66_Output_0", [|2L;2L|],[|2L;2L|],[|0L;0L;0L;0L|],"NOTSET")
                cnn("Convolution110","Pooling66_Output_0","Parameter87","Convolution110_Output_0",[|5L;5L|],[|1L;1L|],"SAME_UPPER",1L,[|1L;1L|])
                add ("Plus112", "Convolution110_Output_0", "Parameter88" ,"Plus112_Output_0")
                relu("ReLU114", "Plus112_Output_0", "ReLU114_Output_0")
                maxPool("Pooling160","ReLU114_Output_0", "Pooling160_Output_0", [|3L;3L|],[|3L;3L|],[|0L;0L;0L;0L|],"NOTSET")
                reshape("Times212_reshape0","Pooling160_Output_0", "Pooling160_Output_0_reshape0_shape","Pooling160_Output_0_reshape0")
                matmul("Times212", "Pooling160_Output_0_reshape0", "Parameter193_reshape1", "Times212_Output_0")
                add("Plus214", "Times212_Output_0", "Parameter194" , "Plus214_Output_0")
            |]

        let tensorProtos = 
            [|
                "Parameter193", DataType.FLOAT32, [|16L; 4L; 4L; 10L|]
                "Parameter87", DataType.FLOAT32, [|16L; 8L; 5L; 5L|]
                "Parameter5", DataType.FLOAT32, [|8L; 1L; 5L; 5L|]
                "Parameter6", DataType.FLOAT32, [|8L; 1L; 1L|]
                "Parameter88", DataType.FLOAT32, [|16L; 1L; 1L|]
                "Pooling160_Output_0_reshape0_shape", DataType.INT64, [|2L|]
                "Parameter193_reshape1_shape", DataType.INT64, [|2L|]
                "Parameter194", DataType.FLOAT32, [|1L; 10L|]
            |] |> Array.map (fun (name, dt,dims) -> 
                let tp = TensorProto(DataType = int dt, Name = name)
                tp.Dims.AddRange(dims)
                let path = Path.Combine(mnistDir, name)
                let data = File.ReadAllBytes(path)
                match dt with
                | DataType.FLOAT32 -> 
                    tp.FloatData.AddRange(data |> bytesToFloats)
                | DataType.INT64 -> 
                    tp.Int64Data.AddRange(data |> bytesToInts)
                | _ -> failwith "err"
                tp)

        let inputs = 
            [| 
                "Input3", DataType.FLOAT32, [|1L;1L;28L;28L|]
                "Parameter5", DataType.FLOAT32, [|8L;1L;5L;5L|]
                "Parameter6", DataType.FLOAT32, [|8L;1L;1L|]
                "Parameter87", DataType.FLOAT32, [|16L;8L;5L;5L|]
                "Parameter88", DataType.FLOAT32, [|16L;1L;1L|]
                "Pooling160_Output_0_reshape0_shape", DataType.INT64, [|2L|]
                "Parameter193",DataType.FLOAT32,[|16L;4L;4L;10L|]
                "Parameter193_reshape1_shape", DataType.INT64,[|2L|]
                "Parameter194", DataType.FLOAT32,[|1L;10L|]
            |]
            |> Array.map (fun (name,dt,shape) -> ValueInfoProto(DocString = "", Name = name, Type = TypeProto(TensorType = TypeProto.Types.Tensor(ElemType = int32 dt, Shape = makeShape shape))))

        let outputs =
            [|"Plus214_Output_0", DataType.FLOAT32,[|1L;10L|]|]
            |> Array.map (fun (name,dt,shape) -> ValueInfoProto(DocString = "", Name = name, Type = TypeProto(TensorType = TypeProto.Types.Tensor(ElemType = int32 dt, Shape = makeShape shape))))

        let valueInfo = 
            [|
                "Parameter193_reshape1", DataType.FLOAT32, [|256L;10L|]
                "Convolution28_Output_0", DataType.FLOAT32, [|1L;8L;28L;28L|]
                "Plus30_Output_0", DataType.FLOAT32, [|1L;8L;28L;28L|]
                "ReLU32_Output_0", DataType.FLOAT32, [|1L;8L;28L;28L|]
                "Pooling66_Output_0", DataType.FLOAT32, [|1L;8L;14L;14L|]
                "Convolution110_Output_0", DataType.FLOAT32, [|1L;16L;14L;14L|]
                "Plus112_Output_0", DataType.FLOAT32, [|1L;16L;14L;14L|]
                "ReLU114_Output_0", DataType.FLOAT32, [|1L;16L;14L;14L|]
                "Pooling160_Output_0", DataType.FLOAT32, [|1L;16L;4L;4L|]
                "Pooling160_Output_0_reshape0", DataType.FLOAT32, [|1L; 256L|]
                "Times212_Output_0", DataType.FLOAT32, [|1L;10L|]
            |]
            |> Array.map (fun (name,dt,shape) -> ValueInfoProto(DocString = "", Name = name, Type = TypeProto(TensorType = TypeProto.Types.Tensor(ElemType = int32 dt, Shape = makeShape shape))))

        let mp = 
            let graph = GraphProto(Name = "CNTKGraph")
            graph.Input.AddRange(inputs)
            graph.Output.AddRange(outputs)
            graph.ValueInfo.AddRange(valueInfo)
            graph.Node.AddRange(nodes)
            graph.Initializer.AddRange(tensorProtos)
            let mp = 
                ModelProto(DocString = "",
                    Domain = "ai.cntk",
                    IrVersion = 3L,
                    ModelVersion = 1L,
                    ProducerName = "CNTK",
                    ProducerVersion = "2.5.1",
                    Graph = graph)
            mp.OpsetImport.Add(OperatorSetIdProto(Version = 8L))
            mp

        let mpData = writeModelToStream(mp)

        mpData |> testModel


    /// NOTE: This is roughly 14x slower with the API overhead
    [<Test>]
    let ``eager mnist``() = 
        let getTensorF(name,shape) =
            let dts = File.ReadAllBytes(Path.Combine(mnistDir, name)) |> bytesToFloats
            on.reshape(ArrayTensorExtensions.ToTensor(dts) ,ArrayTensorExtensions.ToTensor(shape))

        let p193 = getTensorF("Parameter193", [|16L; 4L; 4L; 10L|])
        let p87  = getTensorF("Parameter87",  [|16L; 8L; 5L; 5L|])
        let p5   = getTensorF("Parameter5",  [|8L; 1L; 5L; 5L|])
        let p6   = getTensorF("Parameter6", [|8L; 1L; 1L|])
        let p88  = getTensorF("Parameter88", [|16L; 1L; 1L|])
        let p194 = getTensorF("Parameter194", [|1L; 10L|]) 

        let mnist (x:Tensor<float32>) = 
            let f (x:Tensor<float32>) (p1:Tensor<float32>) (p2:Tensor<float32>) k = 
                on.max_pool(on.relu(on.add(on.conv(x,p1,auto_pad = "SAME_UPPER"),p2)),kernel_shape = [|k;k|], strides = [|k;k|]) |> fst
            on.add(on.mat_mul(on.reshape((f (f x p5 p6 2L) p87 p88 3L),[|1;256|]),on.reshape(p193,[|256;10|])),p194)

        let test_data = 
            let f (x: TensorProto) = x.RawData.ToByteArray() |> bytesToFloats 
            test_data |> Array.map (fun (x,y) -> (f x).ToTensor().Reshape([|1;1;28;28|]), f y)

        for (index,(x,y1)) in Array.indexed(test_data) do
            let y2 = mnist x
            let diff = (y2.ToArray(),y1) ||> Array.zip |> Array.sumBy (fun (x,y) -> System.Math.Abs(x-y))
            if diff > 0.1f then failwithf "Unexpected result in example %i with a difference of %f" index diff

    type ong = ONNXAPIGraph.ONNXGraph

    type MNISTGraph() = 

        let bytesToFloats(buffer : byte[]) = 
            let xs= Array.zeroCreate<float32> (buffer.Length / 4)
            System.Buffer.BlockCopy(buffer, 0, xs, 0, buffer.Length)
            xs

        let getTensorF(name,shape) =
            let dts = File.ReadAllBytes(Path.Combine(mnistDir, name)) |> bytesToFloats
            on.reshape(ArrayTensorExtensions.ToTensor(dts) ,ArrayTensorExtensions.ToTensor(shape))

        let p193 = getTensorF("Parameter193", [|16L; 4L; 4L; 10L|])
        let p87  = getTensorF("Parameter87",  [|16L; 8L; 5L; 5L|])
        let p5   = getTensorF("Parameter5",  [|8L; 1L; 5L; 5L|])
        let p6   = getTensorF("Parameter6", [|8L; 1L; 1L|])
        let p88  = getTensorF("Parameter88", [|16L; 1L; 1L|])
        let p194 = getTensorF("Parameter194", [|1L; 10L|]) 

        [<ReflectedDefinition>]
        member this.Rec(graph:Graph, x:ValueInfo,p1,p2,k) = 
           ong.max_pool(graph,ong.relu(graph,ong.add(graph,ong.conv(graph,x,p1,auto_pad = "SAME_UPPER"),p2)),kernel_shape = [|k;k|], strides = [|k;k|]) |> fst

        [<ReflectedDefinition>]
        member this.Forward(graph: Graph, x: ValueInfo) = 
            let constant (x:Tensor<float32>) = Constants.constant(graph,x)
            ong.add(graph, ong.mat_mul(graph, ong.reshape(graph, (this.Rec (graph, this.Rec(graph, x,constant p5,constant p6,2L),constant p87,constant p88,3L)),Constants.constant(graph,[|1L;256L|].ToTensor())),ong.reshape(graph,constant p193,Constants.constant(graph,[|256L;10L|].ToTensor()))),constant p194)

        [<ReflectedDefinition>]
        member this.Rec(x:Tensor<float32>,p1,p2,k) = 
           on.max_pool(on.relu(on.add(on.conv(x,p1,auto_pad = "SAME_UPPER"),p2)),kernel_shape = [|k;k|], strides = [|k;k|]) |> fst

        [<ReflectedDefinition>]
        member this.Forward(x: Tensor<float32>) = 
            on.add(on.mat_mul(on.reshape((this.Rec (this.Rec(x,p5,p6,2L),p87,p88,3L)),[|1;256|]),on.reshape(p193,[|256;10|])),p194)

    [<Test>]
    let ``full mnist``() = 

        let mnistG = MNISTGraph()
        let makeValueInfoProto(valueInfo: ValueInfo) = 
            ValueInfoProto(Name = valueInfo.name, Type = 
                TypeProto(TensorType = TypeProto.Types.Tensor(ElemType = int32 valueInfo.dt)))

        let input = {name = "Input3";dt=DataType.FLOAT32}
        let graph = Graph.Default()
        let output = mnistG.Forward(graph,input)

        let gp = GraphProto(Name = "G")
        gp.Input.Add(makeValueInfoProto(input))
        gp.Output.Add(makeValueInfoProto(output))
        gp.Node.Add(graph.ops)
        testModel(writeModelToStream(gp |> graphToModel))

    [<Test>]
    let ``converted graph``() = 
        let mnistG = MNISTGraph()
        use graphFunction : DV<Tensor<float32> -> DV<Tensor<float32>>> = Foo.wrapGraph(<@ mnistG.Forward @>)
        testModel2(graphFunction.F)
        testModel2(fun x -> new DV<Tensor<float32>>(mnistG.Forward(x),(fun () -> ())))
        
    type RecA = {a:Tensor<float32>;b:Tensor<float32>}
    type RecB = {a:Tensor<float32>;b:RecA; c:Tensor<float32>*Tensor<float32>}

    [<Test>]
    let ``complex input and output``() = 
        let tupleFunction = <@ fun (x:Tensor<float32>,y:Tensor<float32>) -> (on.add(x,y),on.sub(x,y),on.log(x)) @>
        let recFunction = <@ fun (x:RecA,y:Tensor<float32>) -> {a=x.a;b=x.b},{a = on.add(x.a,x.b); b = x; c = (x.a,x.b)} @>
        use ff : DV<RecA*Tensor<float32> -> DV<RecA*RecB>> = Foo.wrapGraph(recFunction)
        let p1 = [|0.1f|].ToTensor() :> Tensor<float32>
        let x = ({a=p1;b=p1},p1)
        use y = ff.F(x)
        let r1 = (fst y.F).a.ToArray() 
        let diff = (p1.ToArray(),r1) ||> Array.zip |> Array.sumBy (fun (x,y) -> System.Math.Abs(x-y))
        if diff > 0.1f then failwith "Error running function"

//let p1 = [|0.1f|].ToTensor() :> Tensor<float32>
//let x = ({a=p1;b=p1},p1)


//let mnist = MNIST()
//testModel(<@ mnist.Forward @>)
//let ff23 : DV<Tensor<float32> -> DV<Tensor<float32>>> = wrapGraph(<@ mnist.Forward @>)


module ONNXExample = 
    [<Test>]
    let ``squeezenet example``() = 
        let loadTensorFromFile(filename: string) = 
            File.ReadAllLines(filename).[1..]
            |> Array.collect (fun line -> line.Split([|',';'[';']'|], StringSplitOptions.RemoveEmptyEntries))
            |> Array.map Single.Parse

        let dir = Path.Combine(__SOURCE_DIRECTORY__ ,"..", "data", "squeezenet")
        let modelPath = Path.Combine(dir,"squeezenet.onnx")

        // Optional : Create session options and set the graph optimization level for the session
        let options = new SessionOptions()
        options.GraphOptimizationLevel <- GraphOptimizationLevel.ORT_ENABLE_EXTENDED

        use session = new InferenceSession(modelPath, options)
        let inputMeta = session.InputMetadata
        let inputData = loadTensorFromFile(Path.Combine(dir,"bench.in"))
        let container = 
            [|
                for name in inputMeta.Keys do
                    let tensor = new DenseTensor<float32>(Memory.op_Implicit(inputData),ReadOnlySpan.op_Implicit(inputMeta.[name].Dimensions)) 
                    yield NamedOnnxValue.CreateFromTensor<float32>(name, tensor)
            |]
        use results = session.Run(container)

        // TODO verify output
        ()
//        for r in results do
//            printfn "Output for %s" r.Name
//            printfn "%s" (r.AsTensor<float32>().GetArrayString())





