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
    internal class ProtobufNetBuilder : SerializerBuilderBase
    {
        public readonly string ContractAttributeClassName = "ProtoBuf.ProtoContractAttribute";
        public readonly string MemberAttributeClassName = "ProtoBuf.ProtoMemberAttribute";

        public override string Name => "ProtobufNet";

        public override void BuildUpClassHierachy(ClassBuildNode rootNode)
        {
        }

        //public override void BuildUpMessage(MessageBuilder builder)
        //{
        //    builder.UpdateClassDeclaration(
        //        c => c.AddSeparatedAttributes(SyntaxHelper.Attribute(ContractAttributeClassName)));

        //    for (int i = 0; i < builder.MessageProperties.Count; i++)
        //    {
        //        var memberAttr = SyntaxHelper.Attribute(MemberAttributeClassName,
        //            SyntaxFactory.AttributeArgument(SyntaxHelper.LiteralExpression(i + 1)));

        //        builder.UpdatePropertyDeclaration(i, p => p.AddAttributes(memberAttr));
        //    }
        //}

        //public override void CompleteMessageBuilding(ref ClassDeclarationSyntax baseMessageClassDeclaration)
        //{
        //}

        public override ClassDeclarationSyntax GenerateSerializerAdapter(TypeString serilizerClassName, TypeString baseMessageClassName, GeneratorExecutionContext context)
        {
            throw new NotImplementedException();
        }
    }
}
