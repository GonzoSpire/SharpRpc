﻿// Copyright © 2021 Soft-Fx. All rights reserved.
// Author: Andrei Hilevich
//
// This Source Code Form is subject to the terms of the Mozilla
// Public License, v. 2.0. If a copy of the MPL was not distributed
// with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpRpc.Builder
{
    public abstract class SerializerBuilderBase
    {
        public abstract string Name { get; }
        public string EnumVal { get; set; }

        //public abstract void BuildUpMessage(MessageBuilder builder);
        //public abstract void CompleteMessageBuilding(ref ClassDeclarationSyntax baseMessageClassDeclaration);

        public abstract void BuildUpClassHierachy(ClassBuildNode rootNode);

        public abstract ClassDeclarationSyntax GenerateSerializerAdapter(TypeString serilizerClassName, TypeString baseMessageClassName, GeneratorExecutionContext context);
    }
}
