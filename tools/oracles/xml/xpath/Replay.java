import net.sf.saxon.s9api.*;
import net.sf.saxon.regex.*;
import net.sf.saxon.str.*;
import org.w3c.dom.*;
import javax.xml.parsers.*;
import javax.xml.transform.*;
import javax.xml.transform.dom.DOMSource;
import javax.xml.transform.stream.StreamResult;
import java.io.*;
import java.nio.file.*;
import java.util.*;

/** Fixed no-argument development procedure; never linked into GregsEx. */
public final class Replay {
    static final Path ROOT = Path.of("test/corpora/xml/xpath");
    static final Processor PROC = new Processor(false);
    static final DocumentBuilderFactory DOM = DocumentBuilderFactory.newInstance();
    static Document output;
    static Element resultRoot;
    static {
        DOM.setNamespaceAware(true);
        PROC.setConfigurationProperty(net.sf.saxon.lib.Feature.XSD_VERSION,"1.1");
        PROC.getUnderlyingConfiguration().setXMLVersion(10);
        try { DOM.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true); }
        catch (Exception e) { throw new ExceptionInInitializerError(e); }
    }
    static List<Element> children(Element e) {
        var a = new ArrayList<Element>();
        for (Node n=e.getFirstChild(); n!=null; n=n.getNextSibling()) if(n instanceof Element) a.add((Element)n);
        return a;
    }
    static Element child(Element e, String name) {
        for (Element c: children(e)) if (c.getLocalName().equals(name)) return c;
        return null;
    }
    static XdmValue query(String expression) throws SaxonApiException {
        XQueryCompiler c=PROC.newXQueryCompiler(); c.setLanguageVersion("3.1");
        return c.compile(expression).load().evaluate();
    }
    static boolean assertion(Element a, XdmValue v, QName error) throws Exception {
        String tag=a.getLocalName(), text=a.getTextContent();
        if(tag.equals("any-of") || tag.equals("all-of")) {
            boolean all=tag.equals("all-of"), val=all;
            for(Element c: children(a)) { boolean b=assertion(c,v,error); val=all ? val && b : val || b; }
            return val;
        }
        if(tag.equals("error")) return error!=null && (a.getAttribute("code").equals("*") ||
            a.getAttribute("code").equals(error.getLocalName())) && error.getNamespaceUri().toString().equals("http://www.w3.org/2005/xqt-errors");
        if(error!=null) return false;
        String exp=switch(tag) {
            case "assert-true" -> "count($result)=1 and $result instance of xs:boolean and $result";
            case "assert-false" -> "count($result)=1 and $result instance of xs:boolean and not($result)";
            case "assert-empty" -> "empty($result)";
            case "assert-eq" -> "$result eq ("+text+")";
            case "assert-deep-eq" -> "deep-equal($result,("+text+"))";
            case "assert-type" -> "$result instance of "+text;
            case "assert" -> text;
            case "assert-string-value" -> "string-join($result ! string(.),' ') eq $expected";
            case "assert-xml" -> "deep-equal($result,parse-xml($expected)/*)";
            default -> throw new IllegalArgumentException("Unsupported assertion "+tag);
        };
        XPathCompiler c=PROC.newXPathCompiler(); c.setLanguageVersion("3.1");
        c.declareVariable(new QName("result")); c.declareVariable(new QName("expected"));
        c.declareNamespace("fn","http://www.w3.org/2005/xpath-functions");
        c.declareNamespace("err","http://www.w3.org/2005/xqt-errors");
        XPathSelector s=c.compile(exp).load(); s.setVariable(new QName("result"),v);
        s.setVariable(new QName("expected"),new XdmAtomicValue(text));
        return s.effectiveBooleanValue();
    }
    static void evaluate(String id, String expression, Element assertion, String skip) throws Exception {
        Element r=output.createElement("case"); r.setAttribute("id",id); resultRoot.appendChild(r);
        if(skip!=null) { r.setAttribute("status","skipped"); r.setAttribute("reason",skip); return; }
        XdmValue v=null; QName error=null;
        try { v=query(expression); }
        catch(SaxonApiException e) { error=e.getErrorCode(); r.setAttribute("message",e.getMessage());
            if(error!=null) { r.setAttribute("error",error.getLocalName()); r.setAttribute("namespace",error.getNamespaceUri().toString()); } }
        if(v!=null) for(XdmItem i:v) {
            Element item=output.createElement("item"); item.setAttribute("type", i instanceof XdmNode ? "node" : ((XdmAtomicValue)i).getPrimitiveTypeName().getLocalName());
            String value;
            if(i instanceof XdmNode n) { StringWriter w=new StringWriter(); Serializer serializer=PROC.newSerializer(w);
                serializer.setOutputProperty(Serializer.Property.OMIT_XML_DECLARATION,"yes"); serializer.serializeNode(n); value=w.toString(); }
            else value=i.getStringValue();
            // Numeric UTF-16 units preserve CR/LF, supplementary values and empty strings exactly.
            StringJoiner units=new StringJoiner(" "); for(int p=0;p<value.length();p++) units.add(Integer.toString(value.charAt(p)));
            item.setTextContent(units.toString()); r.appendChild(item);
        }
        if(assertion!=null) {
            try { r.setAttribute("status",assertion(assertion,v,error)?"passed":"failed"); }
            catch(Exception e) { r.setAttribute("status","driver-error"); r.setAttribute("reason",e.toString()); }
        } else r.setAttribute("status",error!=null?"error":v!=null?"returned":"driver-error");
    }
    static void direct(Element job) throws Exception {
        XdmValue args=query(job.getTextContent());
        RegularExpression re=PROC.getUnderlyingConfiguration().compileRegularExpression(
            StringView.of(args.itemAt(0).getStringValue()),args.itemAt(2).getStringValue(),"XP30/XSD11",new ArrayList<>());
        if(re.isPlatformNative()) throw new IllegalStateException("Native Java regex forbidden as XPath oracle");
        RegexIterator it=re.analyze(StringView.of(args.itemAt(1).getStringValue()));
        Document doc=DOM.newDocumentBuilder().newDocument();
        Element root=doc.createElementNS("http://www.w3.org/2005/xpath-functions","analyze-string-result"); doc.appendChild(root);
        net.sf.saxon.value.StringValue value;
        // Fixed finite corpus subjects: impose an independent 10,000 segment guard.
        int count=0;
        while((value=it.next())!=null) {
            if(++count>10000) throw new IllegalStateException("oracle segment ceiling");
            Element segment=doc.createElementNS(root.getNamespaceURI(),it.isMatching()?"match":"non-match");root.appendChild(segment);
            if(!it.isMatching()) segment.setTextContent(value.getStringValue());
            else {
                var stack=new ArrayDeque<Element>();stack.push(segment);
                it.processMatchingSubstring(new RegexMatchHandler() {
                    public void characters(UnicodeString v) { stack.peek().appendChild(doc.createTextNode(v.toString())); }
                    public void onGroupStart(int g) { Element el=doc.createElementNS(root.getNamespaceURI(),"group");el.setAttribute("nr",Integer.toString(g));stack.peek().appendChild(el);stack.push(el); }
                    public void onGroupEnd(int g) { stack.pop(); }
                });
            }
        }
        Element r=output.createElement("case");r.setAttribute("id",job.getAttribute("id"));r.setAttribute("status","returned");r.setAttribute("adapter","Saxon direct XP30/XSD11 regex iterator");resultRoot.appendChild(r);
        StringWriter w=new StringWriter();Transformer t=TransformerFactory.newDefaultInstance().newTransformer();t.setOutputProperty(OutputKeys.OMIT_XML_DECLARATION,"yes");t.transform(new DOMSource(doc),new StreamResult(w));
        Element item=output.createElement("item");item.setAttribute("type","node");StringJoiner units=new StringJoiner(" ");String str=w.toString();for(int p=0;p<str.length();p++) units.add(Integer.toString(str.charAt(p)));item.setTextContent(units.toString());r.appendChild(item);
    }
    public static void main(String[] args) throws Exception {
        if(args.length!=0) throw new IllegalArgumentException("No arguments; run fixed replay from repo root.");
        output=DOM.newDocumentBuilder().newDocument(); resultRoot=output.createElement("oracle-results"); output.appendChild(resultRoot);
        resultRoot.setAttribute("processor",PROC.getSaxonEdition()+" "+PROC.getSaxonProductVersion());
        resultRoot.setAttribute("java",System.getProperty("java.runtime.version"));
        resultRoot.setAttribute("xml-version",Integer.toString(PROC.getUnderlyingConfiguration().getXMLVersion()));
        resultRoot.setAttribute("xsd-version",Integer.toString(PROC.getUnderlyingConfiguration().getXsdVersion()));
        resultRoot.setAttribute("language","XQuery3.1/F&O3.1; native Java regex not selected");
        for(String suite:List.of("matches","matches.re","replace","tokenize","analyze-string")) {
            Element root=DOM.newDocumentBuilder().parse(ROOT.resolve("qt3/fn/"+suite+".xml").toFile()).getDocumentElement();
            for(Element tc:children(root)) if(tc.getLocalName().equals("test-case")) {
                String skip=null;
                for(Element d:children(tc)) if(d.getLocalName().equals("dependency")) {
                    String type=d.getAttribute("type"), value=d.getAttribute("value");
                    if(type.equals("feature") && (value.equals("schemaValidation") || value.equals("schemaImport"))) skip="schema-aware query outside regex profile and HE capability";
                    if(type.equals("xml-version") && value.equals("1.1")) skip="XML1.1 outside ratified XML1.0 domain";
                    if(type.equals("xsd-version") && value.equals("1.0")) skip="XSD1.0 dependency outside pinned XSD1.1 regex grammar";
                    if(type.equals("spec") && !value.contains("+")) skip="test restricted to earlier F&O edition";
                }
                if(child(tc,"environment")!=null) skip="external query/schema environment outside typed function adapter";
                evaluate("QT3/"+suite+"/"+tc.getAttribute("name"),child(tc,"test").getTextContent(),children(child(tc,"result")).get(0),skip);
            }
        }
        Element jobs=DOM.newDocumentBuilder().parse(ROOT.resolve("jobs.xml").toFile()).getDocumentElement();
        for(Element job:children(jobs)) { if(job.getAttribute("adapter").equals("direct")) direct(job); else evaluate(job.getAttribute("id"),job.getTextContent(),null,null); }
        Files.createDirectories(Path.of("test/corpora/xml/xpath/oracle"));
        Transformer t=TransformerFactory.newDefaultInstance().newTransformer(); t.setOutputProperty(OutputKeys.INDENT,"yes");
        t.transform(new DOMSource(output),new StreamResult("test/corpora/xml/xpath/oracle/oracle-results.xml"));
        System.out.println("Processor "+resultRoot.getAttribute("processor")+"; preserved "+children(resultRoot).size()+" outcomes.");
    }
}
