module Fable.Fable2Babel

open Fable
open Fable.AST

type ReturnStrategy =
    | Return
    | VarDeclaration of Babel.Identifier * SourceLocation option
    | Assign of Babel.Expression * SourceLocation option

type Import = {
    path: string
    selector: string
    internalFile: string option
}

type Context = {
    file: string
    originalFile: string
    moduleFullName: string
    imports: ResizeArray<Import>
    rootEntitiesPrivateNames: Map<string, string>
}

type IBabelCompiler =
    inherit ICompiler
    abstract DeclarePlugins: (string*IDeclarePlugin) list
    abstract GetProjectAndNamespace: string -> Fable.Project * string
    abstract GetImport: Context -> internalFile: string option -> selector: string -> path: string -> Babel.Expression
    abstract TransformExpr: Context -> Fable.Expr -> Babel.Expression
    abstract TransformStatement: Context -> Fable.Expr -> Babel.Statement
    abstract TransformExprAndResolve: Context -> ReturnStrategy -> Fable.Expr -> Babel.Statement
    abstract TransformFunction: Context -> Fable.Ident list -> Fable.Expr ->
        (Babel.Pattern list) * U2<Babel.BlockStatement, Babel.Expression>
    abstract TransformClass: Context -> SourceLocation option -> Fable.Expr option ->
        Fable.Declaration list -> Babel.ClassExpression
        
and IDeclarePlugin =
    inherit IPlugin
    abstract member TryDeclare:
        com: IBabelCompiler -> ctx: Context -> decl: Fable.Declaration
        -> (Babel.Statement list) option
    abstract member TryDeclareRoot:
        com: IBabelCompiler -> ctx: Context -> file: Fable.File
        -> (U2<Babel.Statement, Babel.ModuleDeclaration> list) option

module Util =
    let (|ExprType|) (fexpr: Fable.Expr) = fexpr.Type
    let (|TransformExpr|) (com: IBabelCompiler) ctx e = com.TransformExpr ctx e
    let (|TransformStatement|) (com: IBabelCompiler) ctx e = com.TransformStatement ctx e
        
    let consBack tail head = head::tail

    let isNull = function
        | Fable.Value Fable.Null | Fable.Wrapped(Fable.Value Fable.Null,_) -> true
        | _ -> false

    // This can cause conflicts in some situations when comparing null to undefined, see #231
    // let cleanNullArgs args =
    //     let rec cleanNull = function
    //         | [] -> []
    //         | (Fable.Value Fable.Null)::args
    //         | (Fable.Wrapped(Fable.Value Fable.Null,_))::args -> cleanNull args
    //         | args -> args
    //     List.rev args |> cleanNull |> List.rev

    let prepareArgs (com: IBabelCompiler) ctx args =
        // cleanNullArgs args
        args |> List.map (function
            | Fable.Value (Fable.Spread expr) ->
                Babel.SpreadElement(com.TransformExpr ctx expr) |> U2.Case2
            | _ as expr -> com.TransformExpr ctx expr |> U2.Case1)
        
    let ident (id: Fable.Ident) =
        Babel.Identifier id.name

    let identFromName name =
        let name = Naming.sanitizeIdent (fun _ -> false) name
        Babel.Identifier name
        
    let sanitizeName propName: Babel.Expression * bool =
        if Naming.identForbiddenCharsRegex.IsMatch propName
        then upcast Babel.StringLiteral propName, true
        else upcast Babel.Identifier propName, false

    let sanitizeProp com ctx = function
        | Fable.Value (Fable.StringConst name)
            when Naming.identForbiddenCharsRegex.IsMatch name = false ->
            Babel.Identifier (name) :> Babel.Expression, false
        | TransformExpr com ctx property -> property, true

    let getCoreLibImport (com: IBabelCompiler) (ctx: Context) coreModule =
        com.Options.coreLib
        |> Path.getExternalImportPath com ctx.file
        |> com.GetImport ctx None coreModule

    let get left propName =
        let expr, computed = sanitizeName propName
        Babel.MemberExpression(left, expr, computed) :> Babel.Expression
        
    let getExpr com ctx (TransformExpr com ctx expr) (property: Fable.Expr) =
        let property, computed = sanitizeProp com ctx property
        match expr with
        | :? Babel.EmptyExpression ->
            match property with
            | :? Babel.StringLiteral as lit ->
                identFromName lit.value :> Babel.Expression
            | _ -> property
        | _ -> Babel.MemberExpression (expr, property, computed) :> Babel.Expression
        
    let rec accessExpr (members: string list) (baseExpr: Babel.Expression option) =
        match baseExpr with
        | Some baseExpr ->
            match members with
            | [] -> baseExpr
            | m::ms -> get baseExpr m |> Some |> accessExpr ms 
        | None ->
            match members with
            // Temporary placeholder to be deleted by getExpr
            | [] -> upcast Babel.EmptyExpression()
            | m::ms -> identFromName m :> Babel.Expression |> Some |> accessExpr ms

    let typeRef (com: IBabelCompiler) ctx (ent: Fable.Entity) (memb: string option): Babel.Expression =
        let getParts ns fullName memb =
            let split (s: string) =
                s.Split('.') |> Array.toList
            let rec removeCommon (xs1: string list) (xs2: string list) =
                match xs1, xs2 with
                | x1::xs1, x2::xs2 when x1 = x2 -> removeCommon xs1 xs2
                | _ -> xs2
            (@) (removeCommon (split ns) (split fullName))
                (match memb with Some memb -> [memb] | None ->  [])
        match ent.File with
        | None -> failwithf "Cannot reference type: %s" ent.FullName
        | Some file when ctx.originalFile <> file ->
            let proj, ns = com.GetProjectAndNamespace file
            let importPath =
                match proj.ImportPath with
                | Some importPath ->
                    let ext = Path.getExternalImportPath com ctx.file importPath
                    let rel = Path.getRelativePath proj.BaseDir file
                    System.IO.Path.Combine(ext, rel)
                    |> Path.normalizePath
                    |> fun x -> System.IO.Path.ChangeExtension(x, null)
                | None ->
                    Path.fixExternalPath com file
                    |> Path.getRelativePath ctx.file
                    |> fun x -> "./" + System.IO.Path.ChangeExtension(x, null)
            getParts ns ent.FullName memb
            |> function
            | [] -> com.GetImport ctx (Some file) "*" importPath
            | memb::parts ->
                com.GetImport ctx (Some file) memb importPath
                |> Some |> accessExpr parts
        | _ ->
            match getParts ctx.moduleFullName ent.FullName memb with
            | rootMemb::parts when Naming.identForbiddenCharsRegex.IsMatch rootMemb ->
                // Check if the root entity is represented internally with a private name
                if ctx.rootEntitiesPrivateNames.ContainsKey(rootMemb)
                then ctx.rootEntitiesPrivateNames.[rootMemb]
                else rootMemb
                |> fun rootMemb -> accessExpr (rootMemb::parts) None
            | parts -> accessExpr parts None

    let buildArray (com: IBabelCompiler) ctx consKind kind =
        match kind with
        | Fable.TypedArray kind ->
            let cons =
                Fable.Util.getTypedArrayName com kind
                |> Babel.Identifier
            let args =
                match consKind with
                | Fable.ArrayValues args ->
                    List.map (com.TransformExpr ctx >> U2.Case1 >> Some) args
                    |> Babel.ArrayExpression :> Babel.Expression |> U2.Case1 |> List.singleton
                | Fable.ArrayAlloc arg ->
                    [U2.Case1 (com.TransformExpr ctx arg)]
            Babel.NewExpression(cons, args) :> Babel.Expression
        | Fable.DynamicArray | Fable.Tuple ->
            match consKind with
            | Fable.ArrayValues args ->
                List.map (com.TransformExpr ctx >> U2.Case1 >> Some) args
                |> Babel.ArrayExpression :> Babel.Expression
            | Fable.ArrayAlloc (TransformExpr com ctx arg) ->
                upcast Babel.NewExpression(Babel.Identifier "Array", [U2.Case1 arg])

    let buildStringArray strings =
        strings
        |> List.map (fun x -> Babel.StringLiteral x :> Babel.Expression |> U2.Case1 |> Some)
        |> Babel.ArrayExpression :> Babel.Expression

    let assign range left right =
        Babel.AssignmentExpression(AssignEqual, left, right, ?loc=range)
        :> Babel.Expression
        
    let block (com: IBabelCompiler) ctx range (exprs: Fable.Expr list) =
        let exprs = match exprs with
                    | [Fable.Sequential (statements,_)] -> statements
                    | _ -> exprs
        Babel.BlockStatement (exprs |> List.map (com.TransformStatement ctx), ?loc=range)
        
    let blockFrom (statement: Babel.Statement) =
        match statement with
        | :? Babel.BlockStatement as block -> block
        | _ -> Babel.BlockStatement([statement], ?loc=statement.loc)

    let returnBlock e =
        Babel.BlockStatement([Babel.ReturnStatement(e, ?loc=e.loc)], ?loc=e.loc)

    // It's important to use arrow functions to lexically bind `this`
    let funcExpression (com: IBabelCompiler) ctx args body =
        let args, body' = com.TransformFunction ctx args body
        Babel.ArrowFunctionExpression (args, body', ?loc=body.Range)
        :> Babel.Expression

    /// Immediately Invoked Function Expression
    let iife (com: IBabelCompiler) ctx (expr: Fable.Expr) =
        Babel.CallExpression (funcExpression com ctx [] expr, [], ?loc=expr.Range)

    let varDeclaration range (var: Babel.Pattern) value =
        Babel.VariableDeclaration (var, value, ?loc=range)
            
    let macroExpression range (txt: string) args =
        Babel.MacroExpression(txt, args, ?loc=range) :> Babel.Expression
        
    let getMemberArgs (com: IBabelCompiler) ctx args body hasRestParams =
        let args, body = com.TransformFunction ctx args body
        let args =
            if not hasRestParams then args else
            let args = List.rev args
            (Babel.RestElement(args.Head) :> Babel.Pattern) :: args.Tail |> List.rev
        let body =
            match body with
            | U2.Case1 e -> e
            | U2.Case2 e -> returnBlock e
        args, body
        // TODO: Optimization: remove null statement that F# compiler adds at the bottom of constructors

    let transformValue (com: IBabelCompiler) ctx r = function
        | Fable.ImportRef (memb, path) ->
            let memb, parts =
                let parts = Array.toList(memb.Split('.'))
                parts.Head, parts.Tail
            Path.getExternalImportPath com ctx.file path
            |> com.GetImport ctx None memb
            |> Some |> accessExpr parts
        | Fable.This -> upcast Babel.ThisExpression ()
        | Fable.Super -> upcast Babel.Super ()
        | Fable.Null -> upcast Babel.NullLiteral ()
        | Fable.IdentValue {name=name} -> upcast Babel.Identifier (name)
        | Fable.NumberConst (x,_) -> upcast Babel.NumericLiteral x
        | Fable.StringConst x -> upcast Babel.StringLiteral (x)
        | Fable.BoolConst x -> upcast Babel.BooleanLiteral (x)
        | Fable.RegexConst (source, flags) -> upcast Babel.RegExpLiteral (source, flags)
        | Fable.Lambda (args, body) -> funcExpression com ctx args body
        | Fable.ArrayConst (cons, kind) -> buildArray com ctx cons kind
        | Fable.Emit emit -> macroExpression None emit []
        | Fable.TypeRef typEnt -> typeRef com ctx typEnt None
        | Fable.Spread _ ->
            failwithf "Unexpected array spread %O" r
        | Fable.LogicalOp _ | Fable.BinaryOp _ | Fable.UnaryOp _ -> 
            failwithf "Unexpected stand-alone operator detected %O" r

    let transformObjectExpr (com: IBabelCompiler) ctx
                            (members, interfaces, baseClass, range): Babel.Expression =
        match baseClass with
        | Some _ as baseClass ->
            members
            |> List.map Fable.MemberDeclaration
            |> com.TransformClass ctx range baseClass
            |> fun c -> upcast Babel.NewExpression(c, [], ?loc=range)
        | None ->
            members |> List.map (fun m ->
                let makeMethod kind name =
                    let name, computed = sanitizeName name
                    let args, body = getMemberArgs com ctx m.Arguments m.Body m.HasRestParams
                    Babel.ObjectMethod(kind, name, args, body, computed, ?loc=Some m.Range)
                    |> U3.Case2
                match m.Kind with
                | Fable.Constructor ->
                    "Unexpected constructor in Object Expression"
                    |> Fable.Util.attachRange range |> failwith
                | Fable.Method name -> makeMethod Babel.ObjectMeth name
                | Fable.Setter name -> makeMethod Babel.ObjectSetter name
                | Fable.Getter (name, false) -> makeMethod Babel.ObjectGetter name
                | Fable.Getter (name, true) ->
                    let key, _ = sanitizeName name
                    Babel.ObjectProperty(key, com.TransformExpr ctx m.Body, ?loc=Some m.Range) |> U3.Case1)
            |> fun props ->
                match interfaces with
                | [] -> props
                | interfaces ->
                    let ifcsSymbol =
                        getCoreLibImport com ctx "Symbol" 
                        |> get <| "interfaces"
                    Babel.ObjectProperty(ifcsSymbol, buildStringArray interfaces, computed=true)
                    |> U3.Case1 |> consBack props
            |> fun props ->
                upcast Babel.ObjectExpression(props, ?loc=range)

    let transformApply com ctx (callee, args, kind, range): Babel.Expression =
        match callee, args with
        // Logical, Binary and Unary Operations
        // If the operation has been wrapped in a lambda, there may be arguments in excess,
        // take that into account in matching patterns
        | Fable.Value (Fable.LogicalOp op), (TransformExpr com ctx left)::(TransformExpr com ctx right)::_ ->
            upcast Babel.LogicalExpression (op, left, right, ?loc=range)
        | Fable.Value (Fable.UnaryOp op), (TransformExpr com ctx operand as expr)::_ ->
            upcast Babel.UnaryExpression (op, operand, ?loc=range)
        | Fable.Value (Fable.BinaryOp op), (TransformExpr com ctx left)::(TransformExpr com ctx right)::_ ->
            upcast Babel.BinaryExpression (op, left, right, ?loc=range)
        // Emit expressions
        | Fable.Value (Fable.Emit emit), args ->
            args |> List.map (function
                | Fable.Value(Fable.Spread expr) -> 
                    Babel.SpreadElement(com.TransformExpr ctx expr, ?loc=expr.Range) :> Babel.Node
                | expr -> com.TransformExpr ctx expr :> Babel.Node)
            |> macroExpression range emit
        // Module or class static members
        | Fable.Value (Fable.TypeRef typEnt), [Fable.Value (Fable.StringConst memb)]
            when kind = Fable.ApplyGet ->
            typeRef com ctx typEnt (Some memb)
        | _ ->
            match kind with
            | Fable.ApplyMeth ->
                Babel.CallExpression (com.TransformExpr ctx callee, prepareArgs com ctx args, ?loc=range)
                :> Babel.Expression
            | Fable.ApplyCons ->
                Babel.NewExpression (com.TransformExpr ctx callee, prepareArgs com ctx args, ?loc=range)
                :> Babel.Expression
            | Fable.ApplyGet ->
                getExpr com ctx callee args.Head

    // TODO: Experimental support for Quotations
    let transformQuote com ctx r expr =
        failwithf "Quotations are not supported %O" r
        // let rec toJson (expr: obj): Babel.Expression =
        //     match expr with 
        //     | :? Babel.Node ->
        //         expr.GetType().GetProperties()
        //         |> Seq.choose (fun p ->
        //             match p.Name with
        //             | "loc" -> None // Remove location to make the object lighter 
        //             | key ->
        //                 let key = Babel.StringLiteral key
        //                 let value = p.GetValue(expr) |> toJson 
        //                 Some(Babel.ObjectProperty(key, value)))
        //         |> Seq.map U3.Case1
        //         |> Seq.toList
        //         |> fun props -> upcast Babel.ObjectExpression(props)
        //     | :? bool as expr -> upcast Babel.BooleanLiteral(expr)
        //     | :? int as expr -> upcast Babel.NumericLiteral(U2.Case1 expr)
        //     | :? float as expr -> upcast Babel.NumericLiteral(U2.Case2 expr)
        //     | :? string as expr -> upcast Babel.StringLiteral(expr)
        //     | expr when Json.isErasedUnion(expr.GetType()) ->
        //         match Json.getErasedUnionValue expr with
        //         | Some v -> toJson v
        //         | None -> upcast Babel.NullLiteral()
        //     | :? System.Collections.IEnumerable as expr ->
        //         let xs = [for x in expr -> U2.Case1(toJson x) |> Some]
        //         upcast Babel.ArrayExpression(xs)
        //     | _ -> failwithf "Unexpected expression inside quote %O" fExpr.Range
        // toJson expr

    let transformStatement com ctx (expr: Fable.Expr): Babel.Statement =
        match expr with
        | Fable.Loop (loopKind, range) ->
            match loopKind with
            | Fable.While (TransformExpr com ctx guard, body) ->
                upcast Babel.WhileStatement (guard, block com ctx body.Range [body], ?loc=range)
            | Fable.ForOf (var, TransformExpr com ctx enumerable, body) ->
                // enumerable doesn't go in VariableDeclator.init but in ForOfStatement.right 
                let var = Babel.VariableDeclaration (ident var)
                upcast Babel.ForOfStatement (
                    U2.Case1 var, enumerable, block com ctx body.Range [body], ?loc=range)
            | Fable.For (var, TransformExpr com ctx start,
                            TransformExpr com ctx limit, body, isUp) ->
                upcast Babel.ForStatement (
                    block com ctx body.Range [body],
                    start |> varDeclaration None (ident var) |> U2.Case1,
                    Babel.BinaryExpression (BinaryOperator.BinaryLessOrEqual, ident var, limit),
                    Babel.UpdateExpression (UpdateOperator.UpdatePlus, false, ident var), ?loc=range)

        | Fable.Set (callee, property, value, range) ->
            let ret =
                match property with
                | None -> Assign(com.TransformExpr ctx callee, range)
                | Some property -> Assign(getExpr com ctx callee property, range)
            com.TransformExprAndResolve ctx ret value

        | Fable.VarDeclaration (var, value, _isMutable) ->
            let ret = VarDeclaration(ident var, expr.Range)
            com.TransformExprAndResolve ctx ret value

        | Fable.TryCatch (body, catch, Some finalizer, range) ->
            upcast Babel.ExpressionStatement (iife com ctx expr, ?loc=expr.Range)

        | Fable.TryCatch (body, catch, finalizer, range) ->
            let handler =
                catch |> Option.map (fun (param, body) ->
                    Babel.CatchClause (ident param,
                        block com ctx body.Range [body], ?loc=body.Range))
            let finalizer =
                finalizer |> Option.map (fun finalizer ->
                    block com ctx body.Range [body])
            upcast Babel.TryStatement (block com ctx expr.Range [body],
                ?handler=handler, ?finalizer=finalizer, ?loc=range)

        | Fable.Throw (TransformExpr com ctx ex, _, range) ->
            upcast Babel.ThrowStatement(ex, ?loc=range)

        | Fable.DebugBreak range ->
            upcast Babel.DebuggerStatement(?loc=range)

        | Fable.IfThenElse (TransformExpr com ctx guardExpr,
                            TransformStatement com ctx thenStament,
                            elseStatement, range) ->
            let elseStatement =
                if isNull elseStatement
                then None else Some(com.TransformStatement ctx elseStatement) 
            upcast Babel.IfStatement(guardExpr, thenStament,
                            ?alternate=elseStatement, ?loc=range)

        | Fable.Sequential(statements, range) ->
            upcast Babel.BlockStatement (
                statements |> List.map (com.TransformStatement ctx), ?loc=range)

        | Fable.Wrapped (expr, _) ->
            com.TransformStatement ctx expr

        // Expressions become ExpressionStatements
        | Fable.Value _ | Fable.Apply _ | Fable.ObjExpr _ | Fable.Quote _ ->
            upcast Babel.ExpressionStatement (com.TransformExpr ctx expr, ?loc=expr.Range)

    let transformExpr (com: IBabelCompiler) ctx (expr: Fable.Expr): Babel.Expression =
        match expr with
        | Fable.Value kind -> transformValue com ctx expr.Range kind
        
        | Fable.ObjExpr (members, interfaces, baseClass, range) ->
            transformObjectExpr com ctx (members, interfaces, baseClass, range) 
        
        | Fable.Wrapped (TransformExpr com ctx expr, _) -> expr
        
        | Fable.Apply (callee, args, kind, _, range) ->
            transformApply com ctx (callee, args, kind, range)
        
        | Fable.IfThenElse (TransformExpr com ctx guardExpr,
                            TransformExpr com ctx thenExpr,
                            TransformExpr com ctx elseExpr, range) ->
            upcast Babel.ConditionalExpression (
                guardExpr, thenExpr, elseExpr, ?loc = range)
        
        | Fable.Set (callee, property, TransformExpr com ctx value, range) ->
            match property with
            | None -> com.TransformExpr ctx callee
            | Some property -> getExpr com ctx callee property
            |> assign range <| value

        // These cannot appear in expression position in JS
        // They must be wrapped in a lambda
        | Fable.Sequential _ | Fable.TryCatch _ | Fable.Throw _
        | Fable.DebugBreak _ | Fable.Loop _ ->
            upcast (iife com ctx expr)

        | Fable.VarDeclaration _ ->
            "Unexpected variable declaration"
            |> Fable.Util.attachRange expr.Range |> failwith
            
        | Fable.Quote quote ->
            transformQuote com ctx expr.Range quote

    let transformExprAndResolve (com: IBabelCompiler) ctx ret
                                 (expr: Fable.Expr): Babel.Statement =
        let resolve strategy expr: Babel.Statement =
            match strategy with
            | Return ->
                upcast Babel.ReturnStatement(expr, ?loc=expr.loc)
            | Assign (left, r) ->
                upcast Babel.ExpressionStatement(assign r left expr, ?loc=r) 
            | VarDeclaration (var, r) ->
                upcast varDeclaration r var expr
        match expr with
        | Fable.Value kind ->
            transformValue com ctx expr.Range kind |> resolve ret
        
        | Fable.ObjExpr (members, interfaces, baseClass, range) ->
            transformObjectExpr com ctx (members, interfaces, baseClass, range)
            |> resolve ret
        
        | Fable.Wrapped (TransformExpr com ctx expr, _) ->
            resolve ret expr
        
        | Fable.Apply (callee, args, kind, _, range) ->
            transformApply com ctx (callee, args, kind, range)
            |> resolve ret
        
        | Fable.IfThenElse (TransformExpr com ctx guardExpr, thenExpr, elseExpr, range) ->
            let elseStatement =
                if isNull elseExpr
                then None else com.TransformExprAndResolve ctx ret elseExpr |> Some
            let thenStatement = com.TransformExprAndResolve ctx ret thenExpr
            Babel.IfStatement(guardExpr, thenStatement,
                    ?alternate=elseStatement, ?loc=range) :> Babel.Statement
        
        | Fable.Sequential (statements, range) ->
            let statements =
                let lasti = (List.length statements) - 1
                statements |> List.mapi (fun i statement ->
                    if i < lasti
                    then com.TransformStatement ctx statement
                    else com.TransformExprAndResolve ctx ret statement)
            upcast Babel.BlockStatement (statements, ?loc=range)
        
        | Fable.TryCatch (body, catch, finalizer, range) ->
            let handler =
                catch |> Option.map (fun (param, body) ->
                    Babel.CatchClause (ident param,
                        com.TransformExprAndResolve ctx ret body |> blockFrom, ?loc=body.Range))
            let finalizer =
                finalizer |> Option.map (fun finalizer ->
                    com.TransformStatement ctx body |> blockFrom)
            upcast Babel.TryStatement (com.TransformExprAndResolve ctx ret body |> blockFrom,
                ?handler=handler, ?finalizer=finalizer, ?loc=range)
        
        // These cannot be resolved (don't return anything)
        // Just compile as a statement
        | Fable.Throw _ | Fable.DebugBreak _ | Fable.Loop _
        | Fable.Set _ | Fable.VarDeclaration _ ->
            com.TransformStatement ctx expr
            
        | Fable.Quote quote ->
            transformQuote com ctx expr.Range quote |> resolve ret

    let transformFunction com ctx args (body: Fable.Expr) =
        let args: Babel.Pattern list =
            List.map (fun x -> upcast ident x) args
        let body: U2<Babel.BlockStatement, Babel.Expression> =
            match body with
            | ExprType (Fable.PrimitiveType Fable.Unit)
            | Fable.Throw _ | Fable.DebugBreak _ | Fable.Loop _ ->
                block com ctx body.Range [body] |> U2.Case1
            | Fable.Sequential _ | Fable.TryCatch _ ->
                transformExprAndResolve com ctx Return body
                |> blockFrom |> U2.Case1
            | _ -> transformExpr com ctx body |> U2.Case2
        args, body
        
    let transformClass com ctx range ident baseClass decls =
        let declareMember range kind name args body isStatic hasRestParams =
            let name, computed = sanitizeName name
            let args, body = getMemberArgs com ctx args body hasRestParams
            Babel.ClassMethod(kind, name, args, body, computed, isStatic, range)
        let baseClass = baseClass |> Option.map (transformExpr com ctx)
        decls
        |> List.map (function
            | Fable.MemberDeclaration m ->
                let kind, name, isStatic =
                    match m.Kind with
                    | Fable.Constructor -> Babel.ClassConstructor, "constructor", false
                    | Fable.Method name -> Babel.ClassFunction, name, m.IsStatic
                    | Fable.Getter (name, _) -> Babel.ClassGetter, name, m.IsStatic
                    | Fable.Setter name -> Babel.ClassSetter, name, m.IsStatic
                declareMember m.Range kind name m.Arguments m.Body isStatic m.HasRestParams
            | Fable.ActionDeclaration _
            | Fable.EntityDeclaration _ as decl ->
                failwithf "Unexpected declaration in class: %A" decl)
        |> List.map U2<_,Babel.ClassProperty>.Case1
        |> fun meths -> Babel.ClassExpression(Babel.ClassBody(meths, ?loc=range),
                            ?id=ident, ?super=baseClass, ?loc=range)

    let declareInterfaces (com: IBabelCompiler) ctx (ent: Fable.Entity) isClass =
        // TODO: For now, we're ignoring compiler generated interfaces for union and records
        let ifcs = ent.Interfaces |> List.filter (fun x ->
            isClass || (not (Naming.automaticInterfaces.Contains x)))
        [ getCoreLibImport com ctx "Util"
          typeRef com ctx ent None
          buildStringArray ifcs
          upcast Babel.StringLiteral ent.FullName ]
        |> fun args ->
            // "$0.setInterfaces($1.prototype, $2, $3)"
            Babel.CallExpression(
                get args.[0] "setInterfaces",
                [get args.[1] "prototype"; args.[2]; args.[3]] |> List.map U2.Case1)
        |> Babel.ExpressionStatement :> Babel.Statement

    let declareEntryPoint com ctx (funcExpr: Babel.Expression) =
        let argv = macroExpression None "process.argv.slice(2)" []
        let main = Babel.CallExpression (funcExpr, [U2.Case1 argv], ?loc=funcExpr.loc) :> Babel.Expression
        // Don't exit the process after leaving main, as there may be a server running
        // Babel.ExpressionStatement(macroExpression funcExpr.loc "process.exit($0)" [main], ?loc=funcExpr.loc)
        Babel.ExpressionStatement(main, ?loc=funcExpr.loc) :> Babel.Statement

    let declareNestedModMember range publicName privateName isPublic isMutable modIdent expr =
        let privateName = defaultArg privateName publicName
        match isPublic, modIdent with
        | true, Some modIdent ->
            // TODO: Define also get-only properties for non-mutable values?
            if isMutable then
                let macro = sprintf "Object.defineProperty($0,'%s',{get:()=>$1,set:x=>$1=x}),$2" publicName
                macroExpression (Some range) macro [modIdent; identFromName privateName; expr]
            else
                assign (Some range) (get modIdent publicName) expr
        | _ -> expr
        |> varDeclaration (Some range) (identFromName privateName) :> Babel.Statement
        |> U2.Case1 |> List.singleton

    let declareRootModMember range publicName privateName isPublic _ _ expr =
        let privateName = defaultArg privateName publicName
        let privateIdent = identFromName privateName
        let decl = varDeclaration (Some range) privateIdent expr :> Babel.Declaration
        match isPublic with
        | false -> U2.Case1 (decl :> Babel.Statement) |> List.singleton
        | true when publicName = privateName ->
            Babel.ExportNamedDeclaration(decl, loc=range)
            :> Babel.ModuleDeclaration |> U2.Case2 |> List.singleton
        | true ->
            // Replace ident forbidden chars of root members, see #207
            let publicName = Naming.replaceIdentForbiddenChars publicName
            let expSpec = Babel.ExportSpecifier(privateIdent, Babel.Identifier publicName)
            let expDecl = Babel.ExportNamedDeclaration(specifiers=[expSpec])
            [expDecl :> Babel.ModuleDeclaration |> U2.Case2; decl :> Babel.Statement |> U2.Case1]

    let transformModMember com ctx declareMember modIdent (m: Fable.Member) =
        let expr, name =
            match m.Kind with
            | Fable.Getter (name, _) ->
                transformExpr com ctx m.Body, name
            | Fable.Method name ->
                funcExpression com ctx m.Arguments m.Body, name
            | Fable.Constructor | Fable.Setter _ ->
                failwithf "Unexpected member in module %O: %A" modIdent m.Kind
        let memberRange =
            match expr.loc with Some loc -> m.Range + loc | None -> m.Range
        if m.TryGetDecorator("EntryPoint").IsSome
        then declareEntryPoint com ctx expr |> U2.Case1 |> List.singleton
        else declareMember memberRange name m.PrivateName m.IsPublic m.IsMutable modIdent expr
        
    let declareClass com ctx declareMember modIdent
                     (ent: Fable.Entity) privateName
                     entDecls entRange baseClass isClass =
        let classDecl =
            // Don't create a new context for class declarations
            let classIdent = identFromName ent.Name |> Some
            transformClass com ctx (Some entRange) classIdent baseClass entDecls
            |> declareMember entRange ent.Name (Some privateName) ent.IsPublic false modIdent
        let classDecl =
            declareInterfaces com ctx ent isClass
            |> fun ifcDecl -> (U2.Case1 ifcDecl)::classDecl
        // Check if there's a static constructor
        entDecls |> Seq.exists (function
            | Fable.MemberDeclaration m ->
                match m.Kind with
                | Fable.Method ".cctor" when m.IsStatic -> true
                | _ -> false
            | _ -> false)
        |> function
        | false -> classDecl
        | true ->
            let cctor = Babel.MemberExpression(
                            typeRef com ctx ent None, Babel.StringLiteral ".cctor", true)
            Babel.ExpressionStatement(Babel.CallExpression(cctor, [])) :> Babel.Statement
            |> U2.Case1 |> consBack classDecl

    let rec transformNestedModule com ctx (ent: Fable.Entity) entDecls entRange =
        let modIdent = Babel.Identifier Naming.exportsIdent 
        let modDecls =
            let ctx = { ctx with moduleFullName = ent.FullName }
            transformModDecls com ctx declareNestedModMember (Some modIdent) entDecls
            |> List.map (function
                | U2.Case1 statement -> statement
                | U2.Case2 _ -> failwith "Unexpected export in nested module")
        Babel.CallExpression(
            Babel.FunctionExpression([modIdent],
                Babel.BlockStatement (modDecls, ?loc=Some entRange),
                ?loc=Some entRange),
            [U2.Case1 (upcast Babel.ObjectExpression [])],
            entRange)

    and transformModDecls (com: IBabelCompiler) ctx declareMember modIdent decls =
        let pluginDeclare decl =
            com.DeclarePlugins |> Seq.tryPick (fun (path, plugin) ->
                try plugin.TryDeclare com ctx decl
                with ex -> failwithf "Error in plugin %s: %s (%O)"
                            path ex.Message decl.Range)
        decls |> List.fold (fun acc decl ->
            match decl with
            | Patterns.Try pluginDeclare statements ->
                (statements |> List.map U2.Case1) @ acc
            | Fable.ActionDeclaration (e,_) ->
                transformStatement com ctx e
                |> U2.Case1
                |> consBack acc
            | Fable.MemberDeclaration m ->
                match m.Kind with
                | Fable.Constructor | Fable.Setter _ -> acc
                | _ -> transformModMember com ctx declareMember modIdent m @ acc
            | Fable.EntityDeclaration (ent, privateName, entDecls, entRange) ->
                match ent.Kind with
                // Interfaces, attribute or erased declarations shouldn't reach this point
                | Fable.Interface ->
                    failwithf "Cannot emit interface declaration: %s" ent.FullName
                | Fable.Class baseClass ->
                    let baseClass = Option.map snd baseClass
                    declareClass com ctx declareMember modIdent
                        ent privateName entDecls entRange baseClass true
                    |> List.append <| acc
                | Fable.Union | Fable.Record | Fable.Exception ->                
                    declareClass com ctx declareMember modIdent
                        ent privateName entDecls entRange None false
                    |> List.append <| acc
                | Fable.Module ->
                    transformNestedModule com ctx ent entDecls entRange
                    |> declareMember entRange ent.Name (Some privateName)
                        ent.IsPublic false modIdent
                    |> List.append <| acc) []
        |> fun decls ->
            match modIdent with
            | None -> decls
            | Some modIdent ->
                Babel.ReturnStatement modIdent
                :> Babel.Statement |> U2.Case1
                |> consBack decls
            |> List.rev
            
    let makeCompiler (com: ICompiler) (projs: Fable.Project list) =
        let declarePlugins =
            com.Plugins |> List.choose (function
                | path, (:? IDeclarePlugin as plugin) -> Some (path, plugin)
                | _ -> None)
        { new IBabelCompiler with
            member bcom.DeclarePlugins =
                declarePlugins
            member bcom.GetProjectAndNamespace fileName =
                projs
                |> Seq.tryPick (fun p ->
                    match Map.tryFind fileName p.FileMap with
                    | None -> None
                    | Some ns -> Some(p, ns))
                |> function
                | Some res -> res
                | None -> failwithf "Cannot find file: %s" fileName                
            member bcom.GetImport ctx internalFile selector path =
                let selector =
                    if selector = "*"
                    then selector
                    // Replace ident forbidden chars of root members, see #207
                    else Naming.replaceIdentForbiddenChars selector
                let i =
                    ctx.imports
                    |> Seq.tryFindIndex (fun imp -> imp.selector = selector && imp.path = path)
                    |> function
                    | Some i -> i
                    | None ->
                        ctx.imports.Add { selector=selector; path=path; internalFile=internalFile }
                        ctx.imports.Count - 1
                upcast Babel.Identifier (Naming.getImportIdent i)
            member bcom.TransformExpr ctx e = transformExpr bcom ctx e
            member bcom.TransformStatement ctx e = transformStatement bcom ctx e
            member bcom.TransformExprAndResolve ctx ret e = transformExprAndResolve bcom ctx ret e
            member bcom.TransformFunction ctx args body = transformFunction bcom ctx args body
            member bcom.TransformClass ctx r baseClass members =
                transformClass bcom ctx r None baseClass members
        interface ICompiler with
            member __.Options = com.Options
            member __.Plugins = com.Plugins
            member __.AddLog msg = com.AddLog msg
            member __.GetLogs() = com.GetLogs() }
            
module Compiler =
    open Util
    open System.IO

    let private getRootEntitiesPrivateNames (decls: #seq<Fable.Declaration>) =
        decls |> Seq.choose (function
            | Fable.EntityDeclaration(ent, privName, _, _) when ent.Name <> privName ->
                Some(ent.Name, privName)
            | _ -> None)
        |> Map

    let transformFile (com: ICompiler) (projs, files) =
        let com = makeCompiler com projs
        files |> Seq.map (fun (file: Fable.File) ->
            try
                // let t = PerfTimer("Fable > Babel")
                let ctx = {
                    file = Path.fixExternalPath com file.FileName
                    originalFile = file.FileName
                    moduleFullName = defaultArg (Map.tryFind file.FileName projs.Head.FileMap) ""
                    rootEntitiesPrivateNames = getRootEntitiesPrivateNames file.Declarations
                    imports = ResizeArray<_>()
                }
                let rootDecls =
                    com.DeclarePlugins
                    |> Seq.tryPick (fun (path, plugin) ->
                        try plugin.TryDeclareRoot com ctx file
                        with ex -> failwithf "Error in plugin %s: %s (%O)"
                                    path ex.Message file.Range)
                    |> function
                    | Some rootDecls -> rootDecls
                    | None -> transformModDecls com ctx declareRootModMember None file.Declarations
                // Add imports
                let rootDecls, dependencies =
                    ctx.imports |> Seq.mapi (fun ident import ->
                        let localId = Babel.Identifier(Naming.getImportIdent ident)
                        let specifier =
                            match import.selector with
                            | "default" | "" ->
                                Babel.ImportDefaultSpecifier(localId)
                                |> U3.Case2
                            | "*" ->
                                Babel.ImportNamespaceSpecifier(localId)
                                |> U3.Case3
                            | memb ->
                                Babel.ImportSpecifier(localId, Babel.Identifier memb)
                                |> U3.Case1
                        Babel.ImportDeclaration([specifier], Babel.StringLiteral import.path)
                        :> Babel.ModuleDeclaration |> U2.Case2, import.internalFile)
                    |> Seq.toList |> List.unzip
                    |> fun (importDecls, dependencies) ->
                        (importDecls@rootDecls), (List.choose id dependencies |> List.distinct)
                // Files not present in the FileMap are injected, no need for source maps
                let originalFileName =
                    if Map.containsKey file.FileName projs.Head.FileMap
                    then file.FileName else null
                Babel.Program(Path.fixExternalPath com file.FileName,
                              originalFileName, file.Range, rootDecls),
                dependencies
            with
            | ex -> exn (sprintf "%s (%s)" ex.Message file.FileName, ex) |> raise
        )