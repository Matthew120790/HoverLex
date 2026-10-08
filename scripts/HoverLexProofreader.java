import java.io.*;
import java.nio.charset.StandardCharsets;
import com.google.gson.*;
import org.languagetool.JLanguageTool;
import org.languagetool.Languages;
import org.languagetool.rules.RuleMatch;

// 只通过匿名管道处理文字，不写入文件或开放网络端口。
public final class HoverLexProofreader {
    public static void main(String[] args) throws Exception {
        PrintWriter output = new PrintWriter(new OutputStreamWriter(System.out, StandardCharsets.UTF_8), true);
        System.setOut(System.err);
        Gson gson = new Gson();
        JLanguageTool tool = new JLanguageTool(Languages.getLanguageForShortCode("en-US"));
        tool.check("Warm up.");
        output.println("{\"ready\":true}");
        try (BufferedReader input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = input.readLine()) != null) {
                JsonObject response = new JsonObject();
                try {
                    JsonObject request = JsonParser.parseString(line).getAsJsonObject();
                    String text = request.get("text").getAsString();
                    if (text.length() > 2000) throw new IllegalArgumentException("Input too long");
                    JsonArray matches = new JsonArray();
                    for (RuleMatch match : tool.check(text)) {
                        JsonObject entry = new JsonObject();
                        entry.addProperty("offset", match.getFromPos());
                        entry.addProperty("length", match.getToPos() - match.getFromPos());
                        JsonObject rule = new JsonObject();
                        rule.addProperty("id", match.getRule().getId());
                        rule.addProperty("issueType", match.getRule().getLocQualityIssueType().toString());
                        entry.add("rule", rule);
                        JsonArray replacements = new JsonArray();
                        for (String replacement : match.getSuggestedReplacements()) {
                            JsonObject value = new JsonObject(); value.addProperty("value", replacement); replacements.add(value);
                        }
                        entry.add("replacements", replacements); matches.add(entry);
                    }
                    response.add("matches", matches);
                } catch (Exception error) {
                    response.addProperty("error", "Proofreading failed");
                }
                output.println(gson.toJson(response));
            }
        }
    }
}
