//
// System.Xml.DTDReader
//
// Author:
//   Atsushi Enomoto  (ginga@kit.hi-ho.ne.jp)
//
// (C)2003 Atsushi Enomoto
//
// FIXME:
//	When a parameter entity contains cp section, it should be closed 
//	within that declaration.
//

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Mono.Xml;
using System.Xml.Schema;
using Mono.Xml.Native;

namespace System.Xml
{
	internal class DTDReader : IXmlLineInfo
	{
		private XmlParserInput currentInput;
		private Stack parserInputStack;

		private string entityReferenceName;

		private char [] nameBuffer;
		private int nameLength;
		private int nameCapacity;
		private const int initialNameCapacity = 256;

		private StringBuilder valueBuffer;

		private int currentLinkedNodeLineNumber;
		private int currentLinkedNodeLinePosition;

		// Parameter entity placeholder
		private int dtdIncludeSect;

		private bool normalization;

		private bool processingInternalSubset;

		string cachedPublicId;
		string cachedSystemId;

		DTDObjectModel DTD;

#if DTD_HANDLE_EVENTS
		public event ValidationEventHandler ValidationEventHandler;
#endif

		// .ctor()

		public DTDReader (DTDObjectModel dtd,
			int startLineNumber, 
			int startLinePosition)
		{
			this.DTD = dtd;
			currentLinkedNodeLineNumber = startLineNumber;
			currentLinkedNodeLinePosition = startLinePosition;
			Init ();
		}

		// Properties

		public string BaseURI {
			get { return currentInput.BaseURI; }
		}

		public bool Normalization {
			get { return normalization; }
			set { normalization = value; }
		}

		// A buffer for ReadContent for ReadOuterXml
//		private StringBuilder CurrentTag {
//			get {
//				return currentInput.CurrentMarkup;
//			}
//		}

		public int LineNumber {
			get { return currentInput.LineNumber; }
		}

		public int LinePosition {
			get { return currentInput.LinePosition; }
		}

		public bool HasLineInfo ()
		{
			return true;
		}

		// Methods

		private void Init ()
		{
			parserInputStack = new Stack ();

			entityReferenceName = String.Empty;

			nameBuffer = new char [initialNameCapacity];
			nameLength = 0;
			nameCapacity = initialNameCapacity;
			
			valueBuffer = new StringBuilder (512);
		}

		internal DTDObjectModel GenerateDTDObjectModel ()
		{
			// now compile DTD
			int originalParserDepth = parserInputStack.Count;
			bool more;
			if (DTD.InternalSubset != null && DTD.InternalSubset.Length > 0) {
				this.processingInternalSubset = true;
				XmlParserInput original = currentInput;

				currentInput = new XmlParserInput (
					new StringReader (DTD.InternalSubset),
					DTD.BaseURI,
					currentLinkedNodeLineNumber,
					currentLinkedNodeLinePosition);
				currentInput.InitialState = false;
				do {
					more = ProcessDTDSubset ();
					if (PeekChar () == -1 && parserInputStack.Count > 0)
						PopParserInput ();
				} while (more || parserInputStack.Count > originalParserDepth);
				if (dtdIncludeSect != 0)
					throw new XmlException (this as IXmlLineInfo,"INCLUDE section is not ended correctly.");

				currentInput = original;
				this.processingInternalSubset = false;
			}
			if (DTD.SystemId != null && DTD.SystemId != String.Empty && DTD.Resolver != null) {
				PushParserInput (DTD.SystemId);
				do {
					more = ProcessDTDSubset ();
					if (PeekChar () == -1 && parserInputStack.Count > 1)
						PopParserInput ();
				} while (more || parserInputStack.Count > originalParserDepth + 1);
				if (dtdIncludeSect != 0)
					throw new XmlException (this as IXmlLineInfo,"INCLUDE section is not ended correctly.");

				PopParserInput ();
			}
			ArrayList sc = new ArrayList ();

			// Entity recursion check.
			foreach (DTDEntityDeclaration ent in DTD.EntityDecls.Values) {
				if (ent.NotationName != null) {
					ent.ScanEntityValue (sc);
					sc.Clear ();
				}
			}
			// release unnecessary memory usage
			DTD.ExternalResources.Clear ();

			return DTD;
		}

		// Read any one of following:
		//   elementdecl, AttlistDecl, EntityDecl, NotationDecl,
		//   PI, Comment, Parameter Entity, or doctype termination char(']')
		//
		// Returns true if it may have any more contents, or false if not.
		private bool ProcessDTDSubset ()
		{
			SkipWhitespace ();
			switch(ReadChar ())
			{
			case -1:
				return false;
			case '%':
				// It affects on entity references' well-formedness
				if (this.processingInternalSubset)
					DTD.InternalSubsetHasPEReference = true;
				string peName = ReadName ();
				Expect (';');
				currentInput.InsertParameterEntityBuffer (" ");
				currentInput.InsertParameterEntityBuffer (GetPEValue (peName));
				currentInput.InsertParameterEntityBuffer (" ");
				int currentLine = currentInput.LineNumber;
				int currentColumn = currentInput.LinePosition;
				while (currentInput.HasPEBuffer)
					ProcessDTDSubset ();
				if (currentInput.LineNumber != currentLine ||
					currentInput.LinePosition != currentColumn)
					throw new XmlException (this as IXmlLineInfo,
						"Incorrectly nested parameter entity.");
				break;
			case '<':
				int c = ReadChar ();
				switch(c)
				{
				case '?':
					// Only read, no store.
					ReadProcessingInstruction ();
					break;
				case '!':
					CompileDeclaration ();
					break;
				case -1:
					throw new XmlException (this as IXmlLineInfo, "Unexpected end of stream.");
				default:
					throw new XmlException (this as IXmlLineInfo, "Syntax Error after '<' character: " + (char) c);
				}
				break;
			case ']':
				if (dtdIncludeSect == 0)
					throw new XmlException (this as IXmlLineInfo, "Unbalanced end of INCLUDE/IGNORE section.");
				// End of inclusion
				Expect ("]>");
				dtdIncludeSect--;
				SkipWhitespace ();
				break;
			default:
				throw new XmlException (this as IXmlLineInfo,String.Format ("Syntax Error inside doctypedecl markup : {0}({1})", PeekChar (), (char) PeekChar ()));
			}
			currentInput.InitialState = false;
			return true;
		}

		private void CompileDeclaration ()
		{
			switch(ReadChar ())
			{
			case '-':
				Expect ('-');
				// Only read, no store.
				ReadComment ();
				break;
			case 'E':
				switch(ReadChar ())
				{
				case 'N':
					Expect ("TITY");
					if (!SkipWhitespace ())
						throw new XmlException (this as IXmlLineInfo,
							"Whitespace is required after '<!ENTITY' in DTD entity declaration.");
					LOOPBACK:
					if (PeekChar () == '%') {
						ReadChar ();
						if (!SkipWhitespace ()) {
							ExpandPERef ();
							goto LOOPBACK;
						} else {
							TryExpandPERef ();
							SkipWhitespace ();
							if (XmlChar.IsNameChar (PeekChar ()))
								ReadParameterEntityDecl ();
							else
								throw new XmlException (this as IXmlLineInfo,"expected name character");
						}
						break;
					}
					DTDEntityDeclaration ent = ReadEntityDecl ();
					if (DTD.EntityDecls [ent.Name] == null)
						DTD.EntityDecls.Add (ent.Name, ent);
					break;
				case 'L':
					Expect ("EMENT");
					DTDElementDeclaration el = ReadElementDecl ();
					DTD.ElementDecls.Add (el.Name, el);
					break;
				default:
					throw new XmlException (this as IXmlLineInfo,"Syntax Error after '<!E' (ELEMENT or ENTITY must be found)");
				}
				break;
			case 'A':
				Expect ("TTLIST");
				DTDAttListDeclaration atl = ReadAttListDecl ();
				DTD.AttListDecls.Add (atl.Name, atl);
				break;
			case 'N':
				Expect ("OTATION");
				DTDNotationDeclaration not = ReadNotationDecl ();
				DTD.NotationDecls.Add (not.Name, not);
				break;
			case '[':
				// conditional sections
				SkipWhitespace ();
				TryExpandPERef ();
				ExpectAfterWhitespace ('I');
				switch (ReadChar ()) {
				case 'N':
					Expect ("CLUDE");
					ExpectAfterWhitespace ('[');
					dtdIncludeSect++;
					break;
				case 'G':
					Expect ("NORE");
					ReadIgnoreSect ();
					break;
				}
				break;
			default:
				throw new XmlException (this as IXmlLineInfo,"Syntax Error after '<!' characters.");
			}
		}

		private void ReadIgnoreSect ()
		{
			ExpectAfterWhitespace ('[');
			int dtdIgnoreSect = 1;

			while (dtdIgnoreSect > 0) {
				switch (ReadChar ()) {
				case -1:
					throw new XmlException (this as IXmlLineInfo,"Unexpected IGNORE section end.");
				case '<':
					if (PeekChar () != '!')
						break;
					ReadChar ();
					if (PeekChar () != '[')
						break;
					ReadChar ();
					dtdIgnoreSect++;
					break;
				case ']':
					if (PeekChar () != ']')
						break;
					ReadChar ();
					if (PeekChar () != '>')
						break;
					ReadChar ();
						dtdIgnoreSect--;
					break;
				}
			}
			if (dtdIgnoreSect != 0)
				throw new XmlException (this as IXmlLineInfo,"IGNORE section is not ended correctly.");
		}

		// The reader is positioned on the head of the name.
		private DTDElementDeclaration ReadElementDecl ()
		{
			DTDElementDeclaration decl = new DTDElementDeclaration (DTD);
			decl.IsInternalSubset = this.processingInternalSubset;

			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between '<!ELEMENT' and name in DTD element declaration.");
			TryExpandPERef ();
			SkipWhitespace ();
			decl.Name = ReadName ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between name and content in DTD element declaration.");
			TryExpandPERef ();
			ReadContentSpec (decl);
			SkipWhitespace ();
			// This expanding is only allowed as a non-validating parser.
			TryExpandPERef ();
			ExpectAfterWhitespace ('>');
			return decl;
		}

		// read 'children'(BNF) of contentspec
		private void ReadContentSpec (DTDElementDeclaration decl)
		{
			TryExpandPERef ();
			SkipWhitespace ();
			switch(ReadChar ())
			{
			case 'E':
				decl.IsEmpty = true;
				Expect ("MPTY");
				break;
			case 'A':
				decl.IsAny = true;
				Expect ("NY");
				break;
			case '(':
				DTDContentModel model = decl.ContentModel;
				SkipWhitespace ();
				TryExpandPERef ();
				SkipWhitespace ();
				if(PeekChar () == '#') {
					// Mixed Contents. "#PCDATA" must appear first.
					decl.IsMixedContent = true;
					model.Occurence = DTDOccurence.ZeroOrMore;
					model.OrderType = DTDContentOrderType.Or;
					Expect ("#PCDATA");
					SkipWhitespace ();
					TryExpandPERef ();
					SkipWhitespace ();
					while(PeekChar () != ')') {
						SkipWhitespace ();
						if (PeekChar () == '%') {
							TryExpandPERef ();
							SkipWhitespace ();
							continue;
						}
						Expect('|');
						SkipWhitespace ();
						TryExpandPERef ();
						SkipWhitespace ();
						DTDContentModel elem = new DTDContentModel (DTD, decl.Name);
//						elem.LineNumber = currentInput.LineNumber;
//						elem.LinePosition = currentInput.LinePosition;
						elem.ElementName = ReadName ();
						this.AddContentModel (model.ChildModels, elem);
						SkipWhitespace ();
						TryExpandPERef ();
						SkipWhitespace ();
					}
					Expect (')');
					if (model.ChildModels.Count > 0)
						Expect ('*');
					else if (PeekChar () == '*')
						Expect ('*');
				} else {
					// Non-Mixed Contents
					model.ChildModels.Add (ReadCP (decl));
					SkipWhitespace ();

					do {	// copied from ReadCP() ...;-)
						if (PeekChar () == '%') {
							TryExpandPERef ();
							SkipWhitespace ();
							continue;
						}
						if(PeekChar ()=='|') {
							// CPType=Or
							if (model.OrderType == DTDContentOrderType.Seq)
								throw new XmlException (this as IXmlLineInfo,
									"Inconsistent choice markup in sequence cp.");
							model.OrderType = DTDContentOrderType.Or;
							ReadChar ();
							SkipWhitespace ();
							AddContentModel (model.ChildModels, ReadCP (decl));
							SkipWhitespace ();
						}
						else if(PeekChar () == ',')
						{
							// CPType=Seq
							if (model.OrderType == DTDContentOrderType.Or)
								throw new XmlException (this as IXmlLineInfo,
									"Inconsistent sequence markup in choice cp.");
							model.OrderType = DTDContentOrderType.Seq;
							ReadChar ();
							SkipWhitespace ();
							model.ChildModels.Add (ReadCP (decl));
							SkipWhitespace ();
						}
						else
							break;
					}
					while(true);

					Expect (')');
					switch(PeekChar ())
					{
					case '?':
						model.Occurence = DTDOccurence.Optional;
						ReadChar ();
						break;
					case '*':
						model.Occurence = DTDOccurence.ZeroOrMore;
						ReadChar ();
						break;
					case '+':
						model.Occurence = DTDOccurence.OneOrMore;
						ReadChar ();
						break;
					}
					SkipWhitespace ();
				}
				SkipWhitespace ();
				break;
			default:
				throw new XmlException (this as IXmlLineInfo, "ContentSpec is missing.");
			}
		}

		// Read 'cp' (BNF) of contentdecl (BNF)
		private DTDContentModel ReadCP (DTDElementDeclaration elem)
		{
			DTDContentModel model = null;
			TryExpandPERef ();
			SkipWhitespace ();
			if(PeekChar () == '(') {
				model = new DTDContentModel (DTD, elem.Name);
				ReadChar ();
				SkipWhitespace ();
				model.ChildModels.Add (ReadCP (elem));
				SkipWhitespace ();
				do {
					if (PeekChar () == '%') {
						TryExpandPERef ();
						SkipWhitespace ();
						continue;
					}
					if(PeekChar ()=='|') {
						// CPType=Or
						if (model.OrderType == DTDContentOrderType.Seq)
							throw new XmlException (this as IXmlLineInfo,
								"Inconsistent choice markup in sequence cp.");
						model.OrderType = DTDContentOrderType.Or;
						ReadChar ();
						SkipWhitespace ();
						AddContentModel (model.ChildModels, ReadCP (elem));
						SkipWhitespace ();
					}
					else if(PeekChar () == ',') {
						// CPType=Seq
						if (model.OrderType == DTDContentOrderType.Or)
							throw new XmlException (this as IXmlLineInfo,
								"Inconsistent sequence markup in choice cp.");
						model.OrderType = DTDContentOrderType.Seq;
						ReadChar ();
						SkipWhitespace ();
						model.ChildModels.Add (ReadCP (elem));
						SkipWhitespace ();
					}
					else
						break;
				}
				while(true);
				ExpectAfterWhitespace (')');
			}
			else {
				TryExpandPERef ();
				model = new DTDContentModel (DTD, elem.Name);
				SkipWhitespace ();
				model.ElementName = ReadName ();
			}

			switch(PeekChar ()) {
			case '?':
				model.Occurence = DTDOccurence.Optional;
				ReadChar ();
				break;
			case '*':
				model.Occurence = DTDOccurence.ZeroOrMore;
				ReadChar ();
				break;
			case '+':
				model.Occurence = DTDOccurence.OneOrMore;
				ReadChar ();
				break;
			}
			return model;
		}

		private void AddContentModel (DTDContentModelCollection cmc, DTDContentModel cm)
		{
			if (cm.ElementName != null) {
				for (int i = 0; i < cmc.Count; i++) {
					if (cmc [i].ElementName == cm.ElementName) {
						HandleError (new XmlSchemaException ("Element content must be unique inside mixed content model.",
							this.LineNumber,
							this.LinePosition,
							null,
							this.BaseURI,
							null));
						return;
					}
				}
			}
			cmc.Add (cm);
		}

		// The reader is positioned on the first name char.
		private void ReadParameterEntityDecl ()
		{
			DTDParameterEntityDeclaration decl = 
				new DTDParameterEntityDeclaration (DTD);
			decl.BaseURI = BaseURI;

			decl.Name = ReadName ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required after name in DTD parameter entity declaration.");

			if (PeekChar () == 'S' || PeekChar () == 'P') {
				// read publicId/systemId
				ReadExternalID ();
				decl.PublicId = cachedPublicId;
				decl.SystemId = cachedSystemId;
				SkipWhitespace ();
				decl.Resolve (this.DTD.Resolver);

				ResolveExternalEntityReplacementText (decl);
			} else {
				TryExpandPERef ();
				int quoteChar = ReadChar ();
				if (quoteChar != '\'' && quoteChar != '"')
					throw new XmlException ("quotation char was expected.");
				ClearValueBuffer ();
				bool loop = true;
				while (loop) {
					int c = ReadChar ();
					switch (c) {
					case -1:
						throw new XmlException ("unexpected end of stream in entity value definition.");
					case '"':
						if (quoteChar == '"')
							loop = false;
						else
							AppendValueChar ('"');
						break;
					case '\'':
						if (quoteChar == '\'')
							loop = false;
						else
							AppendValueChar ('\'');
						break;
					default:
						if (XmlChar.IsInvalid (c))
							throw new XmlException (this as IXmlLineInfo, "Invalid character was used to define parameter entity.");
						AppendValueChar (c);
						break;
					}
				}
				decl.LiteralEntityValue = CreateValueString ();
				ClearValueBuffer ();
				ResolveInternalEntityReplacementText (decl);
			}
			ExpectAfterWhitespace ('>');


			if (DTD.PEDecls [decl.Name] == null) {
                                DTD.PEDecls.Add (decl.Name, decl);
			}
		}

		private void ResolveExternalEntityReplacementText (DTDEntityBase decl)
		{
			if (decl.LiteralEntityValue.StartsWith ("<?xml")) {
				XmlTextReader xtr = new XmlTextReader (decl.LiteralEntityValue, XmlNodeType.Element, null);
				xtr.SkipTextDeclaration ();
				if (decl is DTDEntityDeclaration) {
					// GE - also checked as valid contents
					StringBuilder sb = new StringBuilder ();
					xtr.Normalization = this.Normalization;
					xtr.Read ();
					while (!xtr.EOF)
						sb.Append (xtr.ReadOuterXml ());
					decl.ReplacementText = sb.ToString ();
				}
				else
					// PE
					decl.ReplacementText = xtr.GetRemainder ().ReadToEnd ();
			}
			else
				decl.ReplacementText = decl.LiteralEntityValue;
		}

		private void ResolveInternalEntityReplacementText (DTDEntityBase decl)
		{
			string value = decl.LiteralEntityValue;
			int len = value.Length;
			ClearValueBuffer ();
			for (int i = 0; i < len; i++) {
				int ch = value [i];
				int end = 0;
				string name;
				switch (ch) {
				case '&':
					i++;
					end = value.IndexOf (';', i);
					if (end < i + 1)
						throw new XmlException (decl, "Invalid reference markup.");
					// expand charref
					if (value [i] == '#') {
						i++;
						ch = GetCharacterReference (decl, value, ref i, end);
						if (XmlChar.IsInvalid (ch))
							throw new XmlException (this as IXmlLineInfo, "Invalid character was used to define parameter entity.");

					} else {
						name = value.Substring (i, end - i);
						// don't expand "general" entity.
						AppendValueChar ('&');
						valueBuffer.Append (name);
						AppendValueChar (';');
						i = end;
						break;
					}
					if (XmlChar.IsInvalid (ch))
						throw new XmlException (decl, "Invalid character was found in the entity declaration.");
					AppendValueChar (ch);
					break;
				case '%':
					i++;
					end = value.IndexOf (';', i);
					if (end < i + 1)
						throw new XmlException (decl, "Invalid reference markup.");
					name = value.Substring (i, end - i);
					valueBuffer.Append (GetPEValue (name));
					i = end;
					break;
				default:
					AppendValueChar (ch);
					break;
				}
			}
			decl.ReplacementText = CreateValueString ();

			if (decl is DTDEntityDeclaration) {
				// GE - also checked as valid contents
				XmlTextReader xtr = new XmlTextReader (decl.ReplacementText, XmlNodeType.Element, null);
				StringBuilder sb = new StringBuilder ();
				xtr.Normalization = this.Normalization;
				xtr.Read ();
				while (!xtr.EOF)
					sb.Append (xtr.ReadOuterXml ());
				decl.ReplacementText = sb.ToString ();
			}
			ClearValueBuffer ();
		}

		private int GetCharacterReference (IXmlLineInfo li, string value, ref int index, int end)
		{
			int ret = 0;
			if (value [index] == 'x') {
				try {
					ret = int.Parse (value.Substring (index + 1, end - index - 1), NumberStyles.HexNumber);
				} catch (FormatException) {
					throw new XmlException (li, "Invalid number for a character reference.");
				}
			} else {
				try {
					ret = int.Parse (value.Substring (index, end - index));
				} catch (FormatException) {
					throw new XmlException (li, "Invalid number for a character reference.");
				}
			}
			index = end;
			return ret;
		}

		private string GetPEValue (string peName)
		{
			DTDParameterEntityDeclaration peDecl =
				DTD.PEDecls [peName] as DTDParameterEntityDeclaration;
			if (peDecl != null) {
				if (peDecl.IsInternalSubset)
					throw new XmlException (this as IXmlLineInfo, "Parameter entity is not allowed in internal subset entity '" + peName + "'");
				return peDecl.ReplacementText;
			}
			// See XML 1.0 section 4.1 for both WFC and VC.
			if ((DTD.SystemId == null && !DTD.InternalSubsetHasPEReference) || DTD.IsStandalone)
				throw new XmlException (this as IXmlLineInfo,
					"Parameter entity " + peName + " not found.");
			HandleError (new XmlSchemaException (
				"Parameter entity " + peName + " not found.", null));
			return "";
		}

		private void TryExpandPERef ()
		{
			if (PeekChar () == '%') {
				if (this.processingInternalSubset)
					throw new XmlException (this as IXmlLineInfo, "Parameter entity reference is not allowed inside internal subset.");
				ExpandPERef ();
			}
		}

		// reader is positioned on '%'
		private void ExpandPERef ()
		{
			ReadChar ();
			string peName = ReadName ();
			Expect (';');
			DTDParameterEntityDeclaration peDecl =
				DTD.PEDecls [peName] as DTDParameterEntityDeclaration;
			if (peDecl == null) {
				HandleError (new XmlSchemaException ("Parameter entity " + peName + " not found.", null));
				return;	// do nothing
			}
			currentInput.InsertParameterEntityBuffer (" " + peDecl.ReplacementText + " ");

		}

		// The reader is positioned on the head of the name.
		private DTDEntityDeclaration ReadEntityDecl ()
		{
			DTDEntityDeclaration decl = new DTDEntityDeclaration (DTD);
			decl.IsInternalSubset = this.processingInternalSubset;
			TryExpandPERef ();
			SkipWhitespace ();
			decl.Name = ReadName ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between name and content in DTD entity declaration.");
			TryExpandPERef ();
			SkipWhitespace ();

			if (PeekChar () == 'S' || PeekChar () == 'P') {
				// external entity
				ReadExternalID ();
				decl.PublicId = cachedPublicId;
				decl.SystemId = cachedSystemId;
				if (SkipWhitespace ()) {
					if (PeekChar () == 'N') {
						// NDataDecl
						Expect ("NDATA");
						if (!SkipWhitespace ())
							throw new XmlException (this as IXmlLineInfo,
								"Whitespace is required after NDATA.");
						decl.NotationName = ReadName ();	// ndata_name
					}
				}
				if (decl.NotationName == null) {
					decl.Resolve (this.DTD.Resolver);
					ResolveExternalEntityReplacementText (decl);
				} else {
					// Unparsed entity.
					decl.LiteralEntityValue = String.Empty;
					decl.ReplacementText = String.Empty;
				}
			}
			else {
				// literal entity
				ReadEntityValueDecl (decl);
				ResolveInternalEntityReplacementText (decl);
			}
			SkipWhitespace ();
			// This expanding is only allowed as a non-validating parser.
			TryExpandPERef ();
			ExpectAfterWhitespace ('>');
			return decl;
		}

		private void ReadEntityValueDecl (DTDEntityDeclaration decl)
		{
			SkipWhitespace ();
			// quotation char will be finally removed on unescaping
			int quoteChar = ReadChar ();
			if (quoteChar != '\'' && quoteChar != '"')
				throw new XmlException ("quotation char was expected.");
			ClearValueBuffer ();

			while (PeekChar () != quoteChar) {
				int ch = ReadChar ();
				switch (ch) {
				case '%':
					string name = ReadName ();
					Expect (';');
					if (decl.IsInternalSubset)
						throw new XmlException (this as IXmlLineInfo,
							"Parameter entity is not allowed in internal subset entity '" + name + "'");
					valueBuffer.Append (GetPEValue (name));
					break;
				case -1:
					throw new XmlException ("unexpected end of stream.");
				default:
					if (this.normalization && XmlChar.IsInvalid (ch))
						throw new XmlException (this as IXmlLineInfo, "Invalid character was found in the entity declaration.");
					AppendValueChar (ch);
					break;
				}
			}
//			string value = Dereference (CreateValueString (), false);
			string value = CreateValueString ();
			ClearValueBuffer ();

			Expect (quoteChar);
			decl.LiteralEntityValue = value;
		}

		private DTDAttListDeclaration ReadAttListDecl ()
		{
			TryExpandPERef ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between ATTLIST and name in DTD attlist declaration.");
			TryExpandPERef ();
			SkipWhitespace ();
			string name = ReadName ();	// target element name
			DTDAttListDeclaration decl =
				DTD.AttListDecls [name] as DTDAttListDeclaration;
			if (decl == null)
				decl = new DTDAttListDeclaration (DTD);
			decl.IsInternalSubset = this.processingInternalSubset;
			decl.Name = name;

			if (!SkipWhitespace ())
				if (PeekChar () != '>')
					throw new XmlException (this as IXmlLineInfo,
						"Whitespace is required between name and content in non-empty DTD attlist declaration.");

			TryExpandPERef ();
			SkipWhitespace ();

			while (XmlChar.IsNameChar (PeekChar ())) {
				DTDAttributeDefinition def = ReadAttributeDefinition ();
				// There must not be two or more ID attributes.
				if (def.Datatype.TokenizedType == XmlTokenizedType.ID) {
					for (int i = 0; i < decl.Definitions.Count; i++) {
						DTDAttributeDefinition d = decl [i];
						if (d.Datatype.TokenizedType == XmlTokenizedType.ID) {
							HandleError (new XmlSchemaException ("AttList declaration must not contain two or more ID attributes.",
								def.LineNumber, def.LinePosition, null, def.BaseURI, null));
							break;
						}
					}
				}
				if (decl [def.Name] == null)
					decl.Add (def);
				SkipWhitespace ();
				TryExpandPERef ();
				SkipWhitespace ();
			}
			SkipWhitespace ();
			// This expanding is only allowed as a non-validating parser.
			TryExpandPERef ();
			ExpectAfterWhitespace ('>');
			return decl;
		}

		private DTDAttributeDefinition ReadAttributeDefinition ()
		{
			DTDAttributeDefinition def = new DTDAttributeDefinition (DTD);
			def.IsInternalSubset = this.processingInternalSubset;

			// attr_name
			TryExpandPERef ();
			SkipWhitespace ();
			def.Name = ReadName ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between name and content in DTD attribute definition.");

			// attr_value
			TryExpandPERef ();
			SkipWhitespace ();
			switch(PeekChar ()) {
			case 'C':	// CDATA
				Expect ("CDATA");
				def.Datatype = XmlSchemaDatatype.FromName ("normalizedString");
				break;
			case 'I':	// ID, IDREF, IDREFS
				Expect ("ID");
				if(PeekChar () == 'R') {
					Expect ("REF");
					if(PeekChar () == 'S') {
						// IDREFS
						ReadChar ();
						def.Datatype = XmlSchemaDatatype.FromName ("IDREFS");
					}
					else	// IDREF
						def.Datatype = XmlSchemaDatatype.FromName ("IDREF");
				}
				else	// ID
					def.Datatype = XmlSchemaDatatype.FromName ("ID");
				break;
			case 'E':	// ENTITY, ENTITIES
				Expect ("ENTIT");
				switch(ReadChar ()) {
					case 'Y':	// ENTITY
						def.Datatype = XmlSchemaDatatype.FromName ("ENTITY");
						break;
					case 'I':	// ENTITIES
						Expect ("ES");
						def.Datatype = XmlSchemaDatatype.FromName ("ENTITIES");
						break;
				}
				break;
			case 'N':	// NMTOKEN, NMTOKENS, NOTATION
				ReadChar ();
				switch(PeekChar ()) {
				case 'M':
					Expect ("MTOKEN");
					if(PeekChar ()=='S') {	// NMTOKENS
						ReadChar ();
						def.Datatype = XmlSchemaDatatype.FromName ("NMTOKENS");
					}
					else	// NMTOKEN
						def.Datatype = XmlSchemaDatatype.FromName ("NMTOKEN");
					break;
				case 'O':
					Expect ("OTATION");
					def.Datatype = XmlSchemaDatatype.FromName ("NOTATION");
					if (!SkipWhitespace ())
						throw new XmlException (this as IXmlLineInfo,
							"Whitespace is required between name and content in DTD attribute definition.");
					Expect ('(');
					SkipWhitespace ();
					def.EnumeratedNotations.Add (ReadName ());		// notation name
					SkipWhitespace ();
					while(PeekChar () == '|') {
						ReadChar ();
						SkipWhitespace ();
						def.EnumeratedNotations.Add (ReadName ());	// notation name
						SkipWhitespace ();
					}
					Expect (')');
					break;
				default:
					throw new XmlException ("attribute declaration syntax error.");
				}
				break;
			default:	// Enumerated Values
				def.Datatype = XmlSchemaDatatype.FromName ("NMTOKEN");
				TryExpandPERef ();
				ExpectAfterWhitespace ('(');
				SkipWhitespace ();
				def.EnumeratedAttributeDeclaration.Add (
					def.Datatype.Normalize (ReadNmToken ()));	// enum value
				SkipWhitespace ();
				while(PeekChar () == '|') {
					ReadChar ();
					SkipWhitespace ();
					def.EnumeratedAttributeDeclaration.Add (
						def.Datatype.Normalize (ReadNmToken ()));	// enum value
					SkipWhitespace ();
				}
				Expect (')');
				break;
			}
			TryExpandPERef ();
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between type and occurence in DTD attribute definition.");

			// def_value
			ReadAttributeDefaultValue (def);

			return def;
		}

		private void ReadAttributeDefaultValue (DTDAttributeDefinition def)
		{
			if(PeekChar () == '#')
			{
				ReadChar ();
				switch(PeekChar ())
				{
				case 'R':
					Expect ("REQUIRED");
					def.OccurenceType = DTDAttributeOccurenceType.Required;
					break;
				case 'I':
					Expect ("IMPLIED");
					def.OccurenceType = DTDAttributeOccurenceType.Optional;
					break;
				case 'F':
					Expect ("FIXED");
					def.OccurenceType = DTDAttributeOccurenceType.Fixed;
					if (!SkipWhitespace ())
						throw new XmlException (this as IXmlLineInfo,
							"Whitespace is required between FIXED and actual value in DTD attribute definition.");
					def.UnresolvedDefaultValue = ReadDefaultAttribute ();
					break;
				}
			} else {
				// one of the enumerated value
				SkipWhitespace ();
				TryExpandPERef ();
				SkipWhitespace ();
				def.UnresolvedDefaultValue = ReadDefaultAttribute ();
			}

			// VC: If default value exists, it should be valid.
			if (def.DefaultValue != null) {
				string normalized = def.Datatype.Normalize (def.DefaultValue);
				bool breakup = false;
				object parsed = null;

				// enumeration validity
				if (def.EnumeratedAttributeDeclaration.Count > 0) {
					if (!def.EnumeratedAttributeDeclaration.Contains (normalized)) {
						HandleError (new XmlSchemaException ("Default value is not one of the enumerated values.",
							def.LineNumber, def.LinePosition, null, def.BaseURI, null));
						breakup = true;
					}
				}
				if (def.EnumeratedNotations.Count > 0) {
					if (!def.EnumeratedNotations.Contains (normalized)) {
						HandleError (new XmlSchemaException ("Default value is not one of the enumerated notation values.",
							def.LineNumber, def.LinePosition, null, def.BaseURI, null));
						breakup = true;
					}
				}

				// type based validity
				if (!breakup) {
					try {
						parsed = def.Datatype.ParseValue (normalized, DTD.NameTable, null);
					} catch (Exception ex) { // FIXME: (wishlist) bad catch ;-(
						HandleError (new XmlSchemaException ("Invalid default value for ENTITY type.",
							def.LineNumber, def.LinePosition, null, def.BaseURI, ex));
						breakup = true;
					}
				}
				if (!breakup) {
					switch (def.Datatype.TokenizedType) {
					case XmlTokenizedType.ENTITY:
						if (DTD.EntityDecls [normalized] == null)
							HandleError (new XmlSchemaException ("Specified entity declaration used by default attribute value was not found.",
								def.LineNumber, def.LinePosition, null, def.BaseURI, null));
						break;
					case XmlTokenizedType.ENTITIES:
						string [] entities = parsed as string [];
						for (int i = 0; i < entities.Length; i++) {
							string entity = entities [i];
							if (DTD.EntityDecls [entity] == null)
								HandleError (new XmlSchemaException ("Specified entity declaration used by default attribute value was not found.",
									def.LineNumber, def.LinePosition, null, def.BaseURI, null));
						}
						break;
					}
				}
			}
			// Extra ID attribute validity check.
			if (def.Datatype != null && def.Datatype.TokenizedType == XmlTokenizedType.ID)
				if (def.UnresolvedDefaultValue != null)
					HandleError (new XmlSchemaException ("ID attribute must not have fixed value constraint.",
						def.LineNumber, def.LinePosition, null, def.BaseURI, null));

		}

		private DTDNotationDeclaration ReadNotationDecl()
		{
			DTDNotationDeclaration decl = new DTDNotationDeclaration (DTD);
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required between NOTATION and name in DTD notation declaration.");
			TryExpandPERef ();
			SkipWhitespace ();
			decl.Name = ReadName ();	// notation name
			/*
			if (namespaces) {	// copy from SetProperties ;-)
				int indexOfColon = decl.Name.IndexOf (':');

				if (indexOfColon == -1) {
					decl.Prefix = String.Empty;
					decl.LocalName = decl.Name;
				} else {
					decl.Prefix = decl.Name.Substring (0, indexOfColon);
					decl.LocalName = decl.Name.Substring (indexOfColon + 1);
				}
			} else {
			*/
				decl.Prefix = String.Empty;
				decl.LocalName = decl.Name;
//			}

			SkipWhitespace ();
			if(PeekChar () == 'P') {
				decl.PublicId = ReadPubidLiteral ();
				bool wsSkipped = SkipWhitespace ();
				if (PeekChar () == '\'' || PeekChar () == '"') {
					if (!wsSkipped)
						throw new XmlException (this as IXmlLineInfo,
							"Whitespace is required between public id and system id.");
					decl.SystemId = ReadSystemLiteral (false);
					SkipWhitespace ();
				}
			} else if(PeekChar () == 'S') {
				decl.SystemId = ReadSystemLiteral (true);
				SkipWhitespace ();
			}
			if(decl.PublicId == null && decl.SystemId == null)
				throw new XmlException ("public or system declaration required for \"NOTATION\" declaration.");
			// This expanding is only allowed as a non-validating parser.
			TryExpandPERef ();
			ExpectAfterWhitespace ('>');
			return decl;
		}

		private void ReadExternalID () {
			switch (PeekChar ()) {
			case 'S':
				cachedSystemId = ReadSystemLiteral (true);
				break;
			case 'P':
				cachedPublicId = ReadPubidLiteral ();
				if (!SkipWhitespace ())
					throw new XmlException (this as IXmlLineInfo,
						"Whitespace is required between PUBLIC id and SYSTEM id.");
				cachedSystemId = ReadSystemLiteral (false);
				break;
			}
		}

		// The reader is positioned on the first 'S' of "SYSTEM".
		private string ReadSystemLiteral (bool expectSYSTEM)
		{
			if(expectSYSTEM) {
				Expect ("SYSTEM");
				if (!SkipWhitespace ())
					throw new XmlException (this as IXmlLineInfo,
						"Whitespace is required after 'SYSTEM'.");
			}
			else
				SkipWhitespace ();
			int quoteChar = ReadChar ();	// apos or quot
			int c = 0;
			ClearValueBuffer ();
			while (c != quoteChar) {
				c = ReadChar ();
				if (c < 0)
					throw new XmlException (this as IXmlLineInfo,"Unexpected end of stream in ExternalID.");
				if (c != quoteChar)
					AppendValueChar (c);
			}
			return CreateValueString (); //currentTag.ToString (startPos, currentTag.Length - 1 - startPos);
		}

		private string ReadPubidLiteral()
		{
			Expect ("PUBLIC");
			if (!SkipWhitespace ())
				throw new XmlException (this as IXmlLineInfo,
					"Whitespace is required after 'PUBLIC'.");
			int quoteChar = ReadChar ();
			int c = 0;
			ClearValueBuffer ();
			while(c != quoteChar)
			{
				c = ReadChar ();
				if(c < 0) throw new XmlException (this as IXmlLineInfo,"Unexpected end of stream in ExternalID.");
				if(c != quoteChar && !XmlChar.IsPubidChar (c))
					throw new XmlException (this as IXmlLineInfo,"character '" + (char) c + "' not allowed for PUBLIC ID");
				if (c != quoteChar)
					AppendValueChar (c);
			}
			return CreateValueString (); //currentTag.ToString (startPos, currentTag.Length - 1 - startPos);
		}

		// The reader is positioned on the first character
		// of the name.
		internal string ReadName ()
		{
			return ReadNameOrNmToken(false);
		}

		// The reader is positioned on the first character
		// of the name.
		private string ReadNmToken ()
		{
			return ReadNameOrNmToken(true);
		}

		private string ReadNameOrNmToken(bool isNameToken)
		{
			int ch = PeekChar ();
			if(isNameToken) {
				if (!XmlChar.IsNameChar (ch))
					throw new XmlException (this as IXmlLineInfo,String.Format ("a nmtoken did not start with a legal character {0} ({1})", ch, (char) ch));
			}
			else {
				if (!XmlChar.IsFirstNameChar (ch))
					throw new XmlException (this as IXmlLineInfo,String.Format ("a name did not start with a legal character {0} ({1})", ch, (char) ch));
			}

			nameLength = 0;

			AppendNameChar (ReadChar ());

			while (XmlChar.IsNameChar (PeekChar ())) {
				AppendNameChar (ReadChar ());
			}

			return CreateNameString ();
		}

		// Read the next character and compare it against the
		// specified character.
		private void Expect (int expected)
		{
			int ch = ReadChar ();

			if (ch != expected) {
				throw new XmlException (this as IXmlLineInfo,
					String.Format (
						"expected '{0}' ({1:X}) but found '{2}' ({3:X})",
						(char) expected,
						expected,
						(char) ch,
						ch));
			}
		}

		private void Expect (string expected)
		{
			int len = expected.Length;
			for (int i=0; i< len; i++)
				Expect (expected [i]);
		}

		private void ExpectAfterWhitespace (char c)
		{
			while (true) {
				int i = ReadChar ();
				if (XmlChar.IsWhitespace (i))
					continue;
				if (c != i)
					throw new XmlException (String.Join (String.Empty, new string [] {"Expected ", c.ToString (), ", but found " + (char) i, "[", i.ToString (), "]"}));
				break;
			}
		}

		// Does not consume the first non-whitespace character.
		private bool SkipWhitespace ()
		{
			bool skipped = XmlChar.IsWhitespace (PeekChar ());
			while (XmlChar.IsWhitespace (PeekChar ()))
				ReadChar ();
			return skipped;
		}

		/*
		private string Dereference (string unresolved, bool expandPredefined)
		{
			StringBuilder resolved = new StringBuilder();
			int pos = 0;
			int next = unresolved.IndexOf ('&');
			if(next < 0)
				return unresolved;

			while (next >= 0) {
				if(pos < next)
					resolved.Append (unresolved.Substring (pos, next - pos));// - 1);
				int endPos = unresolved.IndexOf (';', next+1);
				if (endPos < 0)
					throw new XmlException (this as IXmlLineInfo, "Could not resolve entity reference since it did not end with character ';'.");
				string entityName =
					unresolved.Substring (next + 1, endPos - next - 1);
				if(entityName [0] == '#') {
					try {
						int c;
						// character entity
						if(entityName [1] == 'x') {
							// hexadecimal
							c = int.Parse (entityName.Substring (2),
								System.Globalization.NumberStyles.HexNumber);
						} else {
							// decimal
							c = int.Parse (entityName.Substring (1));
						}
						if (c < Char.MaxValue)
							resolved.Append ((char) c);
						else
							resolved.Append (ExpandSurrogateChar (c));
					} catch (FormatException) {
						throw new XmlException (this as IXmlLineInfo, "Invalid character entity reference was found.");
					}
				} else {
					int predefined = XmlChar.GetPredefinedEntity (entityName);
					if (expandPredefined && predefined >= 0)
						resolved.Append (predefined);
					else
					// With respect to "Value", MS document is helpless
					// and the implemention returns inconsistent value
					// (e.g. XML: "&ent; &amp;ent;" ---> Value: "&ent; &ent;".)
						resolved.Append ("&" + entityName + ";");
				}
				pos = endPos + 1;
				if(pos > unresolved.Length)
					break;
				next = unresolved.IndexOf('&', pos);
			}
			resolved.Append (unresolved.Substring(pos));

			return resolved.ToString();
		}
		*/

		private int PeekChar ()
		{
			return currentInput.PeekChar ();
		}

		private int ReadChar ()
		{
			return currentInput.ReadChar ();
		}

		private string ExpandSurrogateChar (int ch)
		{
			if (ch < Char.MaxValue)
				return ((char) ch).ToString ();
			else {
				char [] tmp = new char [] {(char) (ch / 0x10000 + 0xD800 - 1), (char) (ch % 0x10000 + 0xDC00)};
				return new string (tmp);
			}
		}

		// The reader is positioned on the first character after
		// the leading '<!--'.
		private void ReadComment ()
		{
			currentInput.InitialState = false;

			while (PeekChar () != -1) {
				int ch = ReadChar ();

				if (ch == '-' && PeekChar () == '-') {
					ReadChar ();

					if (PeekChar () != '>')
						throw new XmlException (this as IXmlLineInfo,"comments cannot contain '--'");

					ReadChar ();
					break;
				}

				if (XmlChar.IsInvalid (ch))
					throw new XmlException (this as IXmlLineInfo,
						"Not allowed character was found.");
			}
		}

		// The reader is positioned on the first character
		// of the target.
		//
		// It may be xml declaration or processing instruction.
		private void ReadProcessingInstruction ()
		{
			string target = ReadName ();
			if (target == "xml") {
				ReadTextDeclaration ();
				return;
			} else if (target.ToLower () == "xml")
				throw new XmlException (this as IXmlLineInfo,
					"Not allowed processing instruction name which starts with 'X', 'M', 'L' was found.");

			currentInput.InitialState = false;

			if (!SkipWhitespace ())
				if (PeekChar () != '?')
					throw new XmlException (this as IXmlLineInfo,
						"Invalid processing instruction name was found.");

//			ClearValueBuffer ();

			while (PeekChar () != -1) {
				int ch = ReadChar ();

				if (ch == '?' && PeekChar () == '>') {
					ReadChar ();
					break;
				}

//				AppendValueChar ((char)ch);
			}

			/*
			SetProperties (
				XmlNodeType.ProcessingInstruction, // nodeType
				target, // name
				false, // isEmptyElement
				true, // clearAttributes
				valueBuffer // value
			);
			*/
		}

		// The reader is positioned after "<?xml "
		private void ReadTextDeclaration ()
		{
			if (!currentInput.InitialState)
				throw new XmlException (this as IXmlLineInfo,
					"Text declaration cannot appear in this state.");

			currentInput.InitialState = false;

			SkipWhitespace ();

			// version decl
			if (PeekChar () == 'v') {
				Expect ("version");
				ExpectAfterWhitespace ('=');
				SkipWhitespace ();
				int quoteChar = ReadChar ();
				char [] expect1_0 = new char [3];
				int versionLength = 0;
				switch (quoteChar) {
				case '\'':
				case '"':
					while (PeekChar () != quoteChar) {
						if (PeekChar () == -1)
							throw new XmlException (this as IXmlLineInfo,
								"Invalid version declaration inside text declaration.");
						else if (versionLength == 3)
							throw new XmlException (this as IXmlLineInfo,
								"Invalid version number inside text declaration.");
						else {
							expect1_0 [versionLength] = (char) ReadChar ();
							versionLength++;
							if (versionLength == 3 && new String (expect1_0) != "1.0")
								throw new XmlException (this as IXmlLineInfo,
									"Invalid version number inside text declaration.");
						}
					}
					ReadChar ();
					SkipWhitespace ();
					break;
				default:
					throw new XmlException (this as IXmlLineInfo,
						"Invalid version declaration inside text declaration.");
				}
			}

			if (PeekChar () == 'e') {
				Expect ("encoding");
				ExpectAfterWhitespace ('=');
				SkipWhitespace ();
				int quoteChar = ReadChar ();
				switch (quoteChar) {
				case '\'':
				case '"':
					while (PeekChar () != quoteChar)
						if (ReadChar () == -1)
							throw new XmlException (this as IXmlLineInfo,
								"Invalid encoding declaration inside text declaration.");
					ReadChar ();
					SkipWhitespace ();
					break;
				default:
					throw new XmlException (this as IXmlLineInfo,
						"Invalid encoding declaration inside text declaration.");
				}
				// Encoding value should be checked inside XmlInputStream.
			}
			else
				throw new XmlException (this as IXmlLineInfo,
					"Encoding declaration is mandatory in text declaration.");

			Expect ("?>");
		}

		// Note that now this method behaves differently from
		// XmlTextReader's one. It calles AppendValueChar() internally.
		private int ReadCharacterReference ()
		{
			int value = 0;

			if (PeekChar () == 'x') {
				ReadChar ();

				while (PeekChar () != ';' && PeekChar () != -1) {
					int ch = ReadChar ();

					if (ch >= '0' && ch <= '9')
						value = (value << 4) + ch - '0';
					else if (ch >= 'A' && ch <= 'F')
						value = (value << 4) + ch - 'A' + 10;
					else if (ch >= 'a' && ch <= 'f')
						value = (value << 4) + ch - 'a' + 10;
					else
						throw new XmlException (this as IXmlLineInfo,
							String.Format (
								"invalid hexadecimal digit: {0} (#x{1:X})",
								(char) ch,
								ch));
				}
			} else {
				while (PeekChar () != ';' && PeekChar () != -1) {
					int ch = ReadChar ();

					if (ch >= '0' && ch <= '9')
						value = value * 10 + ch - '0';
					else
						throw new XmlException (this as IXmlLineInfo,
							String.Format (
								"invalid decimal digit: {0} (#x{1:X})",
								(char) ch,
								ch));
				}
			}

			ReadChar (); // ';'

			// There is no way to save surrogate pairs...
			if (XmlChar.IsInvalid (value))
				throw new XmlException (this as IXmlLineInfo,
					"Referenced character was not allowed in XML.");
			AppendValueChar (value);
			return value;
		}

		private void AppendNameChar (int ch)
		{
			CheckNameCapacity ();
			if (ch < Char.MaxValue)
				nameBuffer [nameLength++] = (char) ch;
			else {
				nameBuffer [nameLength++] = (char) (ch / 0x10000 + 0xD800 - 1);
				CheckNameCapacity ();
				nameBuffer [nameLength++] = (char) (ch % 0x10000 + 0xDC00);
			}
		}

		private void CheckNameCapacity ()
		{
			if (nameLength == nameCapacity) {
				nameCapacity = nameCapacity * 2;
				char [] oldNameBuffer = nameBuffer;
				nameBuffer = new char [nameCapacity];
				Array.Copy (oldNameBuffer, nameBuffer, nameLength);
			}
		}

		private string CreateNameString ()
		{
			return DTD.NameTable.Add (nameBuffer, 0, nameLength);
		}

		private void AppendValueChar (int ch)
		{
			if (ch < Char.MaxValue)
				valueBuffer.Append ((char) ch);
			else
				valueBuffer.Append (ExpandSurrogateChar (ch));
		}

		private string CreateValueString ()
		{
			return valueBuffer.ToString ();
		}
		
		private void ClearValueBuffer ()
		{
			valueBuffer.Length = 0;
		}

		// The reader is positioned on the quote character.
		// *Keeps quote char* to value to get_QuoteChar() correctly.
		private string ReadDefaultAttribute ()
		{
			ClearValueBuffer ();

			int quoteChar = ReadChar ();

			if (quoteChar != '\'' && quoteChar != '\"')
				throw new XmlException (this as IXmlLineInfo,"an attribute value was not quoted");

			AppendValueChar (quoteChar);

			while (PeekChar () != quoteChar) {
				int ch = ReadChar ();

				switch (ch)
				{
				case '<':
					throw new XmlException (this as IXmlLineInfo,"attribute values cannot contain '<'");
				case -1:
					throw new XmlException (this as IXmlLineInfo,"unexpected end of file in an attribute value");
				case '&':
					AppendValueChar (ch);
					if (PeekChar () == '#')
						break;
					// Check XML 1.0 section 3.1 WFC.
					string entName = ReadName ();
					Expect (';');
					if (XmlChar.GetPredefinedEntity (entName) < 0) {
						DTDEntityDeclaration entDecl = 
							DTD == null ? null : DTD.EntityDecls [entName];
						if (entDecl == null || entDecl.SystemId != null)
							// WFC: Entity Declared (see 4.1)
							if (DTD.IsStandalone || (DTD.SystemId == null && !DTD.InternalSubsetHasPEReference))
								throw new XmlException (this as IXmlLineInfo,
									"Reference to external entities is not allowed in attribute value.");
					}
					valueBuffer.Append (entName);
					AppendValueChar (';');
					break;
				default:
					AppendValueChar (ch);
					break;
				}
			}

			ReadChar (); // quoteChar
			AppendValueChar (quoteChar);

			return CreateValueString ();
		}

		private void PushParserInput (string url)
		{
			Uri baseUri = null;
			try {
				if (DTD.BaseURI != null && DTD.BaseURI.Length > 0)
					baseUri = new Uri (DTD.BaseURI);
			} catch (UriFormatException) {
			}

			Uri absUri = DTD.Resolver.ResolveUri (baseUri, url);
			string absPath = absUri.ToString ();

			foreach (XmlParserInput i in parserInputStack.ToArray ()) {
				if (i.BaseURI == absPath)
					throw new XmlException (this as IXmlLineInfo, "Nested inclusion is not allowed: " + url);
			}
			parserInputStack.Push (currentInput);
			try {
				Stream s = DTD.Resolver.GetEntity (absUri, null, typeof (Stream)) as Stream;
				currentInput = new XmlParserInput (new XmlStreamReader (s), absPath);
			} catch (Exception ex) { // FIXME: (wishlist) Bad exception catch ;-(
				int line = currentInput == null ? 0 : currentInput.LineNumber;
				int col = currentInput == null ? 0 : currentInput.LinePosition;
				string bu = (currentInput == null) ? String.Empty : currentInput.BaseURI;
				HandleError (new XmlSchemaException ("Specified external entity not found. Target URL is " + url + " .",
					line, col, null, bu, ex));
				currentInput = new XmlParserInput (new StringReader (String.Empty), absPath);
			}
		}

		private void PopParserInput ()
		{
			currentInput = parserInputStack.Pop () as XmlParserInput;
		}

		private void HandleError (XmlSchemaException ex)
		{
#if DTD_HANDLE_EVENTS
			if (this.ValidationEventHandler != null)
				ValidationEventHandler (this, new ValidationEventArgs (ex, ex.Message, XmlSeverityType.Error));
#else
			DTD.AddError (ex);
#endif
		}
	}
}
