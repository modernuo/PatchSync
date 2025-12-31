using System.Net.Http;
using Spectre.Console;

namespace PatchSync.CLI.Commands;

/// <summary>
/// Preview different Figlet fonts and title styles for the CLI.
/// Interactive navigation: Left/Right for fonts, Up/Down for styles.
/// </summary>
public static class FontPreviewCommand
{
    private static readonly string FontCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchSync", "fonts");

    // Base URLs for font repositories
    private const string XeroBase = "https://raw.githubusercontent.com/xero/figlet-fonts/main/";
    private const string HimeiBase = "https://raw.githubusercontent.com/hIMEI29A/FigletFonts/master/src/";
    private const string PhrackerBase = "https://raw.githubusercontent.com/phracker/figlet-fonts/master/";

    /// <summary>
    /// Available Figlet fonts - comprehensive list from multiple repositories.
    /// Deduplicated by normalized name (treating _ and - as equivalent, case-insensitive).
    /// </summary>
    private static readonly (string Name, string File, string Url)[] AvailableFonts =
    [
        // === Numbers & Symbols ===
        ("1Row", "1Row.flf", XeroBase + "1Row.flf"),
        ("3-D", "3-D.flf", XeroBase + "3-D.flf"),
        ("3D-ASCII", "3D-ASCII.flf", HimeiBase + "3D-ASCII.flf"),
        ("3x5", "3x5.flf", XeroBase + "3x5.flf"),
        ("4Max", "4Max.flf", XeroBase + "4Max.flf"),
        ("4x4 Offr", "4x4_offr.flf", HimeiBase + "4x4_offr.flf"),
        ("5 Line Oblique", "5lineoblique.flf", XeroBase + "5lineoblique.flf"),
        ("5x7", "5x7.flf", HimeiBase + "5x7.flf"),
        ("5x8", "5x8.flf", HimeiBase + "5x8.flf"),
        ("6x10", "6x10.flf", HimeiBase + "6x10.flf"),
        ("6x9", "6x9.flf", HimeiBase + "6x9.flf"),

        // === A ===
        ("Acrobatic", "Acrobatic.flf", XeroBase + "Acrobatic.flf"),
        ("Alligator", "Alligator.flf", XeroBase + "Alligator.flf"),
        ("Alligator2", "Alligator2.flf", XeroBase + "Alligator2.flf"),
        ("Alligator3", "alligator3.flf", XeroBase + "alligator3.flf"),
        ("Alpha", "Alpha.flf", XeroBase + "Alpha.flf"),
        ("Alphabet", "Alphabet.flf", XeroBase + "Alphabet.flf"),
        ("AMC 3 Line", "amc3line.flf", XeroBase + "amc3line.flf"),
        ("AMC 3 Liv1", "amc3liv1.flf", XeroBase + "amc3liv1.flf"),
        ("AMC AAA01", "amcaaa01.flf", XeroBase + "amcaaa01.flf"),
        ("AMC Neko", "amcneko.flf", XeroBase + "amcneko.flf"),
        ("AMC Razo2", "amcrazo2.flf", XeroBase + "amcrazo2.flf"),
        ("AMC Razor", "amcrazor.flf", XeroBase + "amcrazor.flf"),
        ("AMC Slash", "amcslash.flf", XeroBase + "amcslash.flf"),
        ("AMC Slider", "amcslder.flf", XeroBase + "amcslder.flf"),
        ("AMC Thin", "amcthin.flf", XeroBase + "amcthin.flf"),
        ("AMC Tubes", "amctubes.flf", XeroBase + "amctubes.flf"),
        ("AMC Untitled", "amcun1.flf", XeroBase + "amcun1.flf"),
        ("ANSI Shadow", "ANSI_Shadow.flf", HimeiBase + "ANSI_Shadow.flf"),
        ("Arrows", "Arrows.flf", XeroBase + "Arrows.flf"),
        ("ASCII New Roman", "ascii_new_roman.flf", XeroBase + "ascii_new_roman.flf"),
        ("Avatar", "Avatar.flf", XeroBase + "Avatar.flf"),

        // === B ===
        ("B1FF", "B1FF.flf", XeroBase + "B1FF.flf"),
        ("Banner", "Banner.flf", XeroBase + "Banner.flf"),
        ("Banner3", "Banner3.flf", XeroBase + "Banner3.flf"),
        ("Banner3-D", "Banner3-D.flf", XeroBase + "Banner3-D.flf"),
        ("Banner4", "Banner4.flf", XeroBase + "Banner4.flf"),
        ("Barbwire", "Barbwire.flf", XeroBase + "Barbwire.flf"),
        ("Basic", "Basic.flf", XeroBase + "Basic.flf"),
        ("Bear", "Bear.flf", XeroBase + "Bear.flf"),
        ("Bell", "Bell.flf", XeroBase + "Bell.flf"),
        ("Benjamin", "Benjamin.flf", XeroBase + "Benjamin.flf"),
        ("Big", "Big.flf", XeroBase + "Big.flf"),
        ("Big Chief", "bigchief.flf", XeroBase + "bigchief.flf"),
        ("Big Money NE", "Big_Money-ne.flf", HimeiBase + "Big_Money-ne.flf"),
        ("Big Money NW", "Big_Money-nw.flf", HimeiBase + "Big_Money-nw.flf"),
        ("Big Money SE", "Big_Money-se.flf", HimeiBase + "Big_Money-se.flf"),
        ("Big Money SW", "Big_Money-sw.flf", HimeiBase + "Big_Money-sw.flf"),
        ("Bigfig", "Bigfig.flf", XeroBase + "Bigfig.flf"),
        ("Binary", "Binary.flf", XeroBase + "Binary.flf"),
        ("Block", "Block.flf", XeroBase + "Block.flf"),
        ("Blocks", "Blocks.flf", XeroBase + "Blocks.flf"),
        ("Bloody", "Bloody.flf", XeroBase + "Bloody.flf"),
        ("Bolger", "Bolger.flf", XeroBase + "Bolger.flf"),
        ("Braced", "Braced.flf", XeroBase + "Braced.flf"),
        ("Bright", "Bright.flf", XeroBase + "Bright.flf"),
        ("Broadway", "Broadway.flf", XeroBase + "Broadway.flf"),
        ("Broadway KB", "broadway_kb.flf", XeroBase + "broadway_kb.flf"),
        ("Bubble", "Bubble.flf", XeroBase + "Bubble.flf"),
        ("Bulbhead", "Bulbhead.flf", XeroBase + "Bulbhead.flf"),

        // === C ===
        ("Caligraphy", "Caligraphy.flf", XeroBase + "Caligraphy.flf"),
        ("Caligraphy2", "calgphy2.flf", XeroBase + "calgphy2.flf"),
        ("Calvin S", "Calvin_S.flf", HimeiBase + "Calvin_S.flf"),
        ("Cards", "Cards.flf", XeroBase + "Cards.flf"),
        ("Catwalk", "Catwalk.flf", XeroBase + "Catwalk.flf"),
        ("Chiseled", "Chiseled.flf", XeroBase + "Chiseled.flf"),
        ("Chunky", "Chunky.flf", XeroBase + "Chunky.flf"),
        ("Coinstak", "Coinstak.flf", XeroBase + "Coinstak.flf"),
        ("Cola", "Cola.flf", XeroBase + "Cola.flf"),
        ("Colossal", "Colossal.flf", XeroBase + "Colossal.flf"),
        ("Computer", "Computer.flf", XeroBase + "Computer.flf"),
        ("Contessa", "Contessa.flf", XeroBase + "Contessa.flf"),
        ("Contrast", "Contrast.flf", XeroBase + "Contrast.flf"),
        ("Cosmic", "cosmic.flf", XeroBase + "cosmic.flf"),
        ("Cosmike", "Cosmike.flf", XeroBase + "Cosmike.flf"),
        ("Crawford", "Crawford.flf", XeroBase + "Crawford.flf"),
        ("Crawford2", "Crawford2.flf", HimeiBase + "Crawford2.flf"),
        ("Crazy", "Crazy.flf", XeroBase + "Crazy.flf"),
        ("Cricket", "Cricket.flf", XeroBase + "Cricket.flf"),
        ("Cursive", "Cursive.flf", XeroBase + "Cursive.flf"),
        ("Cyberlarge", "Cyberlarge.flf", XeroBase + "Cyberlarge.flf"),
        ("Cybermedium", "Cybermedium.flf", XeroBase + "Cybermedium.flf"),
        ("Cybersmall", "Cybersmall.flf", XeroBase + "Cybersmall.flf"),
        ("Cygnet", "Cygnet.flf", XeroBase + "Cygnet.flf"),

        // === D ===
        ("DANC4", "DANC4.flf", XeroBase + "DANC4.flf"),
        ("Dancing Font", "dancingfont.flf", XeroBase + "dancingfont.flf"),
        ("Decimal", "Decimal.flf", XeroBase + "Decimal.flf"),
        ("Def Leppard", "defleppard.flf", XeroBase + "defleppard.flf"),
        ("Delta Corps Priest", "Delta_Corps_Priest_1.flf", HimeiBase + "Delta_Corps_Priest_1.flf"),
        ("Diamond", "Diamond.flf", XeroBase + "Diamond.flf"),
        ("Diet Cola", "dietcola.flf", XeroBase + "dietcola.flf"),
        ("Digital", "Digital.flf", XeroBase + "Digital.flf"),
        ("Doh", "Doh.flf", XeroBase + "Doh.flf"),
        ("Doom", "Doom.flf", XeroBase + "Doom.flf"),
        ("DOS Rebel", "dosrebel.flf", XeroBase + "dosrebel.flf"),
        ("Dot Matrix", "dotmatrix.flf", XeroBase + "dotmatrix.flf"),
        ("Double", "Double.flf", XeroBase + "Double.flf"),
        ("Double Shorts", "doubleshorts.flf", XeroBase + "doubleshorts.flf"),
        ("Dr Pepper", "drpepper.flf", XeroBase + "drpepper.flf"),
        ("DWhistled", "dwhistled.flf", PhrackerBase + "dwhistled.flf"),

        // === E ===
        ("Efti Chess", "eftichess.flf", XeroBase + "eftichess.flf"),
        ("Efti Font", "eftifont.flf", XeroBase + "eftifont.flf"),
        ("Efti Italic", "Efti_Italic.flf", HimeiBase + "Efti_Italic.flf"),
        ("Efti Piti", "eftipiti.flf", XeroBase + "eftipiti.flf"),
        ("Efti Robot", "eftirobot.flf", XeroBase + "eftirobot.flf"),
        ("Efti Wall", "eftiwall.flf", XeroBase + "eftiwall.flf"),
        ("Efti Water", "eftiwater.flf", XeroBase + "eftiwater.flf"),
        ("Electronic", "Electronic.flf", XeroBase + "Electronic.flf"),
        ("Elite", "Elite.flf", XeroBase + "Elite.flf"),
        ("Epic", "Epic.flf", XeroBase + "Epic.flf"),

        // === F ===
        ("Fender", "Fender.flf", XeroBase + "Fender.flf"),
        ("Filter", "Filter.flf", XeroBase + "Filter.flf"),
        ("Fire Font-K", "fire_font-k.flf", XeroBase + "fire_font-k.flf"),
        ("Fire Font-S", "fire_font-s.flf", XeroBase + "fire_font-s.flf"),
        ("Flipped", "Flipped.flf", XeroBase + "Flipped.flf"),
        ("Flower Power", "flowerpower.flf", XeroBase + "flowerpower.flf"),
        ("Four Tops", "fourtops.flf", XeroBase + "fourtops.flf"),
        ("Fraktur", "Fraktur.flf", XeroBase + "Fraktur.flf"),
        ("Fun Face", "funface.flf", XeroBase + "funface.flf"),
        ("Fun Faces", "funfaces.flf", XeroBase + "funfaces.flf"),
        ("Fuzzy", "Fuzzy.flf", XeroBase + "Fuzzy.flf"),

        // === G ===
        ("Georgi16", "Georgi16.flf", XeroBase + "Georgi16.flf"),
        ("Georgia11", "Georgia11.flf", XeroBase + "Georgia11.flf"),
        ("Ghost", "Ghost.flf", XeroBase + "Ghost.flf"),
        ("Ghoulish", "Ghoulish.flf", XeroBase + "Ghoulish.flf"),
        ("Glenyn", "Glenyn.flf", XeroBase + "Glenyn.flf"),
        ("Goofy", "Goofy.flf", XeroBase + "Goofy.flf"),
        ("Gothic", "Gothic.flf", XeroBase + "Gothic.flf"),
        ("Graceful", "Graceful.flf", XeroBase + "Graceful.flf"),
        ("Gradient", "Gradient.flf", XeroBase + "Gradient.flf"),
        ("Graffiti", "Graffiti.flf", XeroBase + "Graffiti.flf"),
        ("Greek", "Greek.flf", XeroBase + "Greek.flf"),

        // === H ===
        ("Heart Left", "heart_left.flf", XeroBase + "heart_left.flf"),
        ("Heart Right", "heart_right.flf", XeroBase + "heart_right.flf"),
        ("Henry 3D", "henry3d.flf", XeroBase + "henry3d.flf"),
        ("Hex", "Hex.flf", XeroBase + "Hex.flf"),
        ("Hieroglyphs", "Hieroglyphs.flf", XeroBase + "Hieroglyphs.flf"),
        ("Hollywood", "Hollywood.flf", XeroBase + "Hollywood.flf"),
        ("Horizontal Left", "horizontalleft.flf", XeroBase + "horizontalleft.flf"),
        ("Horizontal Right", "horizontalright.flf", XeroBase + "horizontalright.flf"),

        // === I ===
        ("ICL-1900", "ICL-1900.flf", XeroBase + "ICL-1900.flf"),
        ("Impossible", "Impossible.flf", XeroBase + "Impossible.flf"),
        ("Invita", "Invita.flf", XeroBase + "Invita.flf"),
        ("Isometric1", "Isometric1.flf", XeroBase + "Isometric1.flf"),
        ("Isometric2", "Isometric2.flf", XeroBase + "Isometric2.flf"),
        ("Isometric3", "Isometric3.flf", XeroBase + "Isometric3.flf"),
        ("Isometric4", "Isometric4.flf", XeroBase + "Isometric4.flf"),
        ("Italic", "Italic.flf", XeroBase + "Italic.flf"),
        ("Ivrit", "Ivrit.flf", XeroBase + "Ivrit.flf"),

        // === J ===
        ("JS Block Letters", "JS_Block_Letters.flf", HimeiBase + "JS_Block_Letters.flf"),
        ("JS Bracket Letters", "JS_Bracket_Letters.flf", HimeiBase + "JS_Bracket_Letters.flf"),
        ("JS Capital Curves", "JS_Capital_Curves.flf", HimeiBase + "JS_Capital_Curves.flf"),
        ("JS Cursive", "JS_Cursive.flf", HimeiBase + "JS_Cursive.flf"),
        ("JS Stick Letters", "JS_Stick_Letters.flf", HimeiBase + "JS_Stick_Letters.flf"),
        ("Jacky", "Jacky.flf", XeroBase + "Jacky.flf"),
        ("Jazmine", "Jazmine.flf", XeroBase + "Jazmine.flf"),
        ("Jerusalem", "Jerusalem.flf", XeroBase + "Jerusalem.flf"),

        // === K ===
        ("Katakana", "Katakana.flf", XeroBase + "Katakana.flf"),
        ("Kban", "Kban.flf", XeroBase + "Kban.flf"),
        ("Keyboard", "Keyboard.flf", XeroBase + "Keyboard.flf"),
        ("Knob", "Knob.flf", XeroBase + "Knob.flf"),
        ("Konto", "Konto.flf", XeroBase + "Konto.flf"),
        ("Konto Slant", "kontoslant.flf", XeroBase + "kontoslant.flf"),

        // === L ===
        ("Larry 3D", "larry3d.flf", XeroBase + "larry3d.flf"),
        ("Larry 3D 2", "Larry_3D_2.flf", HimeiBase + "Larry_3D_2.flf"),
        ("LCD", "LCD.flf", XeroBase + "LCD.flf"),
        ("Lean", "Lean.flf", XeroBase + "Lean.flf"),
        ("Letters", "Letters.flf", XeroBase + "Letters.flf"),
        ("Lil Devil", "lildevil.flf", XeroBase + "lildevil.flf"),
        ("Line Blocks", "lineblocks.flf", XeroBase + "lineblocks.flf"),
        ("Linux", "Linux.flf", XeroBase + "Linux.flf"),
        ("Lockergnome", "Lockergnome.flf", XeroBase + "Lockergnome.flf"),

        // === M ===
        ("Madrid", "Madrid.flf", XeroBase + "Madrid.flf"),
        ("Marquee", "Marquee.flf", XeroBase + "Marquee.flf"),
        ("Maxfour", "Maxfour.flf", XeroBase + "Maxfour.flf"),
        ("Merlin1", "Merlin1.flf", XeroBase + "Merlin1.flf"),
        ("Merlin2", "Merlin2.flf", XeroBase + "Merlin2.flf"),
        ("Mike", "Mike.flf", XeroBase + "Mike.flf"),
        ("Mini", "Mini.flf", XeroBase + "Mini.flf"),
        ("Mirror", "Mirror.flf", XeroBase + "Mirror.flf"),
        ("Mnemonic", "Mnemonic.flf", XeroBase + "Mnemonic.flf"),
        ("Modular", "Modular.flf", XeroBase + "Modular.flf"),
        ("Morse", "Morse.flf", XeroBase + "Morse.flf"),
        ("Morse2", "Morse2.flf", XeroBase + "Morse2.flf"),
        ("Moscow", "Moscow.flf", XeroBase + "Moscow.flf"),
        ("Mshebrew210", "Mshebrew210.flf", XeroBase + "Mshebrew210.flf"),
        ("Muzzle", "Muzzle.flf", XeroBase + "Muzzle.flf"),

        // === N ===
        ("Nancyj", "Nancyj.flf", XeroBase + "Nancyj.flf"),
        ("Nancyj-Fancy", "Nancyj-Fancy.flf", XeroBase + "Nancyj-Fancy.flf"),
        ("Nancyj-Improved", "Nancyj-Improved.flf", XeroBase + "Nancyj-Improved.flf"),
        ("Nancyj-Underlined", "Nancyj-Underlined.flf", XeroBase + "Nancyj-Underlined.flf"),
        ("Nipples", "Nipples.flf", XeroBase + "Nipples.flf"),
        ("NScript", "nscript.flf", XeroBase + "nscript.flf"),
        ("NT Greek", "ntgreek.flf", XeroBase + "ntgreek.flf"),
        ("NV Script", "nvscript.flf", XeroBase + "nvscript.flf"),

        // === O ===
        ("O8", "O8.flf", XeroBase + "O8.flf"),
        ("Octal", "Octal.flf", XeroBase + "Octal.flf"),
        ("Ogre", "Ogre.flf", XeroBase + "Ogre.flf"),
        ("Old Banner", "oldbanner.flf", XeroBase + "oldbanner.flf"),
        ("OS2", "OS2.flf", XeroBase + "OS2.flf"),

        // === P ===
        ("Patorjk-HeX", "Patorjk-HeX.flf", HimeiBase + "Patorjk-HeX.flf"),
        ("Pawp", "Pawp.flf", XeroBase + "Pawp.flf"),
        ("Peaks", "Peaks.flf", XeroBase + "Peaks.flf"),
        ("Peaks Slant", "peaksslant.flf", XeroBase + "peaksslant.flf"),
        ("Pebbles", "Pebbles.flf", XeroBase + "Pebbles.flf"),
        ("Pepper", "Pepper.flf", XeroBase + "Pepper.flf"),
        ("Poison", "Poison.flf", XeroBase + "Poison.flf"),
        ("Puffy", "Puffy.flf", XeroBase + "Puffy.flf"),
        ("Puzzle", "Puzzle.flf", XeroBase + "Puzzle.flf"),
        ("Pyramid", "Pyramid.flf", XeroBase + "Pyramid.flf"),

        // === R ===
        ("Rammstein", "Rammstein.flf", XeroBase + "Rammstein.flf"),
        ("Rectangles", "Rectangles.flf", XeroBase + "Rectangles.flf"),
        ("Red Phoenix", "red_phoenix.flf", XeroBase + "red_phoenix.flf"),
        ("Relief", "Relief.flf", XeroBase + "Relief.flf"),
        ("Relief2", "Relief2.flf", XeroBase + "Relief2.flf"),
        ("Reverse", "Reverse.flf", XeroBase + "Reverse.flf"),
        ("Roman", "Roman.flf", XeroBase + "Roman.flf"),
        ("Rot13", "Rot13.flf", XeroBase + "Rot13.flf"),
        ("Rotated", "Rotated.flf", XeroBase + "Rotated.flf"),
        ("Rounded", "Rounded.flf", XeroBase + "Rounded.flf"),
        ("Rowan Cap", "rowancap.flf", XeroBase + "rowancap.flf"),
        ("Rozzo", "Rozzo.flf", XeroBase + "Rozzo.flf"),
        ("Runic", "Runic.flf", XeroBase + "Runic.flf"),
        ("Runyc", "Runyc.flf", XeroBase + "Runyc.flf"),

        // === S ===
        ("S Blood", "sblood.flf", XeroBase + "sblood.flf"),
        ("S-Relief", "s-relief.flf", PhrackerBase + "s-relief.flf"),
        ("Santa Clara", "santaclara.flf", XeroBase + "santaclara.flf"),
        ("Script", "Script.flf", XeroBase + "Script.flf"),
        ("Serifcap", "Serifcap.flf", XeroBase + "Serifcap.flf"),
        ("Shadow", "Shadow.flf", XeroBase + "Shadow.flf"),
        ("Shimrod", "Shimrod.flf", XeroBase + "Shimrod.flf"),
        ("Short", "Short.flf", XeroBase + "Short.flf"),
        ("Slant", "Slant.flf", XeroBase + "Slant.flf"),
        ("Slant Relief", "Slant_Relief.flf", HimeiBase + "Slant_Relief.flf"),
        ("Slide", "Slide.flf", XeroBase + "Slide.flf"),
        ("SL Script", "slscript.flf", XeroBase + "slscript.flf"),
        ("Small", "Small.flf", XeroBase + "Small.flf"),
        ("Small Caps", "smallcaps.flf", XeroBase + "smallcaps.flf"),
        ("Small Isometric1", "smisome1.flf", XeroBase + "smisome1.flf"),
        ("Small Keyboard", "smkeyboard.flf", XeroBase + "smkeyboard.flf"),
        ("Small Poison", "smpoison.flf", XeroBase + "smpoison.flf"),
        ("Small Script", "smscript.flf", XeroBase + "smscript.flf"),
        ("Small Shadow", "smshadow.flf", XeroBase + "smshadow.flf"),
        ("Small Slant", "smslant.flf", XeroBase + "smslant.flf"),
        ("Small Tengwar", "smtengwar.flf", XeroBase + "smtengwar.flf"),
        ("Soft", "Soft.flf", XeroBase + "Soft.flf"),
        ("Speed", "Speed.flf", XeroBase + "Speed.flf"),
        ("Spliff", "Spliff.flf", XeroBase + "Spliff.flf"),
        ("Stacey", "Stacey.flf", XeroBase + "Stacey.flf"),
        ("Stampate", "Stampate.flf", XeroBase + "Stampate.flf"),
        ("Stampatello", "Stampatello.flf", XeroBase + "Stampatello.flf"),
        ("Standard", "Standard.flf", XeroBase + "Standard.flf"),
        ("Star Strips", "starstrips.flf", XeroBase + "starstrips.flf"),
        ("Star Wars", "starwars.flf", XeroBase + "starwars.flf"),
        ("Stellar", "Stellar.flf", XeroBase + "Stellar.flf"),
        ("Stencil1", "stencil1.flf", HimeiBase + "stencil1.flf"),
        ("Stencil2", "stencil2.flf", HimeiBase + "stencil2.flf"),
        ("Stforek", "Stforek.flf", XeroBase + "Stforek.flf"),
        ("Stick Letters", "Stick_Letters.flf", HimeiBase + "Stick_Letters.flf"),
        ("Stop", "Stop.flf", XeroBase + "Stop.flf"),
        ("Straight", "Straight.flf", XeroBase + "Straight.flf"),
        ("Stronger Than All", "Stronger_Than_All.flf", HimeiBase + "Stronger_Than_All.flf"),
        ("Sub-Zero", "Sub-Zero.flf", XeroBase + "Sub-Zero.flf"),
        ("Swamp Land", "swampland.flf", XeroBase + "swampland.flf"),
        ("Swan", "Swan.flf", XeroBase + "Swan.flf"),
        ("Sweet", "Sweet.flf", XeroBase + "Sweet.flf"),

        // === T ===
        ("Tanja", "Tanja.flf", XeroBase + "Tanja.flf"),
        ("Tengwar", "Tengwar.flf", XeroBase + "Tengwar.flf"),
        ("Term", "Term.flf", XeroBase + "Term.flf"),
        ("Test1", "Test1.flf", XeroBase + "Test1.flf"),
        ("The Edge", "The_Edge.flf", HimeiBase + "The_Edge.flf"),
        ("Thick", "Thick.flf", XeroBase + "Thick.flf"),
        ("Thin", "Thin.flf", XeroBase + "Thin.flf"),
        ("THIS", "THIS.flf", HimeiBase + "THIS.flf"),
        ("Thorned", "Thorned.flf", HimeiBase + "Thorned.flf"),
        ("Three Point", "threepoint.flf", XeroBase + "threepoint.flf"),
        ("Ticks", "Ticks.flf", XeroBase + "Ticks.flf"),
        ("Ticks Slant", "ticksslant.flf", XeroBase + "ticksslant.flf"),
        ("Tiles", "Tiles.flf", XeroBase + "Tiles.flf"),
        ("Tinker-Toy", "Tinker-Toy.flf", XeroBase + "Tinker-Toy.flf"),
        ("Tombstone", "Tombstone.flf", XeroBase + "Tombstone.flf"),
        ("Train", "Train.flf", XeroBase + "Train.flf"),
        ("Trek", "Trek.flf", XeroBase + "Trek.flf"),
        ("Tsalagi", "Tsalagi.flf", XeroBase + "Tsalagi.flf"),
        ("Tubular", "Tubular.flf", XeroBase + "Tubular.flf"),
        ("Twisted", "Twisted.flf", XeroBase + "Twisted.flf"),
        ("Two Point", "twopoint.flf", XeroBase + "twopoint.flf"),

        // === U ===
        ("Univers", "Univers.flf", XeroBase + "Univers.flf"),
        ("USA Flag", "usaflag.flf", XeroBase + "usaflag.flf"),

        // === V ===
        ("Varsity", "Varsity.flf", XeroBase + "Varsity.flf"),

        // === W ===
        ("Wavy", "Wavy.flf", XeroBase + "Wavy.flf"),
        ("Weird", "Weird.flf", XeroBase + "Weird.flf"),
        ("Wet Letter", "wetletter.flf", XeroBase + "wetletter.flf"),
        ("Whimsy", "Whimsy.flf", XeroBase + "Whimsy.flf"),
        ("Wow", "Wow.flf", XeroBase + "Wow.flf"),
    ];

    /// <summary>
    /// Color/style presets for the Figlet text
    /// </summary>
    private static readonly (string Name, Color Color, string? Decoration)[] ColorStyles =
    [
        ("Blue", Color.Blue, null),
        ("Cyan", Color.Cyan1, null),
        ("Green (Matrix)", Color.Green, null),
        ("Hot Pink", Color.DeepPink1, null),
        ("Purple", Color.MediumPurple1, null),
        ("Orange", Color.Orange1, null),
        ("Yellow", Color.Yellow, null),
        ("White", Color.White, null),
        ("Red", Color.Red, null),
        ("Grey", Color.Grey, null),
        ("Cyan + Line", Color.Cyan1, "line"),
        ("Green + Box", Color.Green, "box-double"),
        ("Purple + Box", Color.Fuchsia, "box-heavy"),
        ("Blue + Box", Color.Blue, "box-rounded"),
        ("Cyberpunk", Color.Yellow, "cyber"),
        ("Glitch", Color.Red, "glitch"),
        ("Retro Terminal", Color.Green, "terminal"),
        ("Hacker", Color.Green, "hacker"),
        ("Neon Sign", Color.Magenta1, "neon"),
        ("Rainbow", Color.Cyan1, "rainbow"),
    ];

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, FigletFont?> _fontCache = new();

    public static Task<int> RunAsync(string[] args)
    {
        var parser = new ArgParser(args);

        if (parser.HasHelp)
        {
            ShowHelp();
            return Task.FromResult(0);
        }

        var text = parser.Get("text") ?? "PatchSync";
        RunInteractivePreview(text);
        return Task.FromResult(0);
    }

    public static Task<int> RunWizardAsync()
    {
        var text = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Text to preview[/] [grey](Enter for PatchSync)[/]:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(text))
            text = "PatchSync";

        RunInteractivePreview(text);
        return Task.FromResult(0);
    }

    private static void RunInteractivePreview(string text)
    {
        var fontIndex = 0;
        var styleIndex = 0;
        var running = true;

        // Ensure cache directory exists
        Directory.CreateDirectory(FontCacheDir);

        // Hide cursor for cleaner display
        Console.CursorVisible = false;

        try
        {
            while (running)
            {
                RenderPreview(text, fontIndex, styleIndex);

                var key = Console.ReadKey(true);

                switch (key.Key)
                {
                    // Font navigation (Left/Right)
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.H: // vim-style
                        fontIndex = (fontIndex - 1 + AvailableFonts.Length) % AvailableFonts.Length;
                        break;

                    case ConsoleKey.RightArrow:
                    case ConsoleKey.L: // vim-style
                        fontIndex = (fontIndex + 1) % AvailableFonts.Length;
                        break;

                    // Style navigation (Up/Down)
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K: // vim-style
                        styleIndex = (styleIndex - 1 + ColorStyles.Length) % ColorStyles.Length;
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J: // vim-style
                        styleIndex = (styleIndex + 1) % ColorStyles.Length;
                        break;

                    case ConsoleKey.Home:
                        fontIndex = 0;
                        styleIndex = 0;
                        break;

                    case ConsoleKey.End:
                        fontIndex = AvailableFonts.Length - 1;
                        styleIndex = ColorStyles.Length - 1;
                        break;

                    case ConsoleKey.PageUp:
                        fontIndex = Math.Max(0, fontIndex - 10);
                        break;

                    case ConsoleKey.PageDown:
                        fontIndex = Math.Min(AvailableFonts.Length - 1, fontIndex + 10);
                        break;

                    case ConsoleKey.Enter:
                        running = false;
                        ShowSelectedStyle(text, fontIndex, styleIndex);
                        break;

                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        running = false;
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        break;

                    // Tab to cycle through fonts quickly
                    case ConsoleKey.Tab:
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                            fontIndex = (fontIndex - 1 + AvailableFonts.Length) % AvailableFonts.Length;
                        else
                            fontIndex = (fontIndex + 1) % AvailableFonts.Length;
                        break;

                    // Spacebar for random font
                    case ConsoleKey.Spacebar:
                        fontIndex = Random.Shared.Next(AvailableFonts.Length);
                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private static void RenderPreview(string text, int fontIndex, int styleIndex)
    {
        AnsiConsole.Clear();

        var (fontName, fontFile, _) = AvailableFonts[fontIndex];
        var (styleName, color, decoration) = ColorStyles[styleIndex];
        var termInfo = GetTerminalInfo();

        // Header
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold yellow]  PATCHSYNC FONT & STYLE PREVIEW[/]");
        AnsiConsole.MarkupLine("[grey]═══════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.WriteLine();

        // Navigation info
        AnsiConsole.MarkupLine($"[grey]Terminal:[/] {termInfo}");
        AnsiConsole.MarkupLine($"[grey]Font:[/] [cyan]{fontIndex + 1}[/][grey]/[/][cyan]{AvailableFonts.Length}[/]    [grey]Style:[/] [cyan]{styleIndex + 1}[/][grey]/[/][cyan]{ColorStyles.Length}[/]");
        AnsiConsole.WriteLine();

        // Current selection
        AnsiConsole.MarkupLine($"[yellow]Font:[/] [bold white]{fontName}[/]  [grey]│[/]  [yellow]Style:[/] [bold white]{styleName}[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Render the preview
        try
        {
            var font = GetOrDownloadFont(fontIndex);
            if (font == null && _incompatibleFonts.Contains(AvailableFonts[fontIndex].File))
            {
                AnsiConsole.MarkupLine("[yellow]This font is incompatible with Spectre.Console's parser.[/]");
                AnsiConsole.MarkupLine("[grey]Using default font instead:[/]");
                AnsiConsole.WriteLine();
            }
            RenderWithStyle(text, font, color, decoration);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();

        // Controls
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]←/→[/][grey] or [/][white]h/l[/][grey]  Change font       [/][white]↑/↓[/][grey] or [/][white]j/k[/][grey]  Change style[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]PgUp/PgDn[/][grey]    Skip 10 fonts      [/][white]Space[/][grey]        Random font[/]");
        AnsiConsole.MarkupLine("[grey]  [/][white]Enter[/][grey]        Select             [/][white]Esc/q[/][grey]        Cancel[/]");
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");

        // Font and style lists side by side
        AnsiConsole.WriteLine();
        RenderLists(fontIndex, styleIndex);
    }

    private static void RenderLists(int fontIndex, int styleIndex)
    {
        // Show 5 fonts and 5 styles side by side
        var fontStart = Math.Max(0, fontIndex - 2);
        var fontEnd = Math.Min(AvailableFonts.Length - 1, fontStart + 4);
        fontStart = Math.Max(0, fontEnd - 4);

        var styleStart = Math.Max(0, styleIndex - 2);
        var styleEnd = Math.Min(ColorStyles.Length - 1, styleStart + 4);
        styleStart = Math.Max(0, styleEnd - 4);

        var lines = Math.Max(fontEnd - fontStart + 1, styleEnd - styleStart + 1);

        AnsiConsole.MarkupLine("[grey]  Fonts                              Styles[/]");
        AnsiConsole.MarkupLine("[grey]  ─────                              ──────[/]");

        for (int i = 0; i < lines; i++)
        {
            var fontLine = "";
            var styleLine = "";

            var fi = fontStart + i;
            if (fi <= fontEnd && fi < AvailableFonts.Length)
            {
                var (name, _, _) = AvailableFonts[fi];
                var displayName = name.Length > 20 ? name[..17] + "..." : name;
                if (fi == fontIndex)
                    fontLine = $"[cyan]►[/] [bold white]{displayName,-20}[/]";
                else
                    fontLine = $"  [grey]{displayName,-20}[/]";
            }
            else
            {
                fontLine = new string(' ', 22);
            }

            var si = styleStart + i;
            if (si <= styleEnd && si < ColorStyles.Length)
            {
                var (name, _, _) = ColorStyles[si];
                var displayName = name.Length > 18 ? name[..15] + "..." : name;
                if (si == styleIndex)
                    styleLine = $"[cyan]►[/] [bold white]{displayName}[/]";
                else
                    styleLine = $"  [grey]{displayName}[/]";
            }

            AnsiConsole.MarkupLine($"  {fontLine}       {styleLine}");
        }
    }

    // Track fonts that are incompatible with Spectre.Console's parser
    private static readonly HashSet<string> _incompatibleFonts = new();
    private const string IncompatibleMarker = ".incompatible";

    private static FigletFont? GetOrDownloadFont(int fontIndex)
    {
        var (name, file, url) = AvailableFonts[fontIndex];
        var cachePath = Path.Combine(FontCacheDir, file);
        var incompatiblePath = cachePath + IncompatibleMarker;

        // Check memory cache
        if (_fontCache.TryGetValue(file, out var cachedFont))
            return cachedFont;

        // Check if marked as incompatible
        if (_incompatibleFonts.Contains(file) || File.Exists(incompatiblePath))
        {
            _incompatibleFonts.Add(file);
            _fontCache[file] = null;
            return null;
        }

        // Check disk cache
        if (File.Exists(cachePath))
        {
            try
            {
                var font = FigletFont.Load(cachePath);
                _fontCache[file] = font;
                return font;
            }
            catch
            {
                // Font file exists but is incompatible with Spectre.Console
                MarkAsIncompatible(file, incompatiblePath);
                return null;
            }
        }

        // Download font
        try
        {
            AnsiConsole.MarkupLine($"[grey]Downloading font: {name}...[/]");
            var content = _httpClient.GetStringAsync(url).GetAwaiter().GetResult();

            // Ensure parent directory exists (for nested filenames)
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(cachePath, content);

            // Try to parse the font
            try
            {
                var font = FigletFont.Load(cachePath);
                _fontCache[file] = font;
                return font;
            }
            catch (Exception parseEx)
            {
                // Downloaded but incompatible with Spectre.Console parser
                AnsiConsole.MarkupLine($"[yellow]Font incompatible: {Markup.Escape(parseEx.Message)}[/]");
                MarkAsIncompatible(file, incompatiblePath);
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Download failed: {Markup.Escape(ex.Message)}[/]");
            _fontCache[file] = null;
            return null;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Error: {Markup.Escape(ex.Message)}[/]");
            _fontCache[file] = null;
            return null;
        }
    }

    private static void MarkAsIncompatible(string file, string incompatiblePath)
    {
        _incompatibleFonts.Add(file);
        _fontCache[file] = null;
        // Create marker file so we don't keep re-trying
        try { File.WriteAllText(incompatiblePath, "Incompatible with Spectre.Console FigletFont parser"); }
        catch { /* ignore */ }
    }

    private static void RenderWithStyle(string text, FigletFont? font, Color color, string? decoration)
    {
        var figlet = font != null
            ? new FigletText(font, text).Color(color)
            : new FigletText(text).Color(color);

        switch (decoration)
        {
            case "line":
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine($"[{color.ToMarkup()}]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
                break;

            case "box-double":
                var panelDouble = new Panel(figlet)
                    .Border(BoxBorder.Double)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelDouble);
                break;

            case "box-heavy":
                var panelHeavy = new Panel(figlet)
                    .Border(BoxBorder.Heavy)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelHeavy);
                break;

            case "box-rounded":
                var panelRounded = new Panel(figlet)
                    .Border(BoxBorder.Rounded)
                    .BorderColor(color)
                    .Padding(1, 0);
                AnsiConsole.Write(panelRounded);
                break;

            case "cyber":
                AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[fuchsia]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/]");
                break;

            case "glitch":
                AnsiConsole.MarkupLine("[red on black]█▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀█[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[cyan]░▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/][cyan]▒▓█▓▒░[/][red]▒▓█▓▒░[/][green]▒▓█▓▒░[/]");
                break;

            case "terminal":
                AnsiConsole.MarkupLine("[green]┌─────────────────────────────────────────────────────────────────────────┐[/]");
                AnsiConsole.MarkupLine("[green]│[/] [black on green] SYSTEM READY [/]                                                       [green]│[/]");
                AnsiConsole.MarkupLine("[green]├─────────────────────────────────────────────────────────────────────────┤[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[green]└─────────────────────────────────────────────────────────────────────────┘[/]");
                break;

            case "hacker":
                AnsiConsole.MarkupLine("[green]> INITIALIZING...[/]");
                AnsiConsole.MarkupLine("[green]> LOADING SYSTEM...[/]");
                AnsiConsole.MarkupLine("[green]> ACCESS GRANTED[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[green]> _[/]");
                break;

            case "neon":
                AnsiConsole.MarkupLine("[grey]     ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[grey]     ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛[/]");
                AnsiConsole.MarkupLine("[magenta1]                    ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧ ✦ ✧[/]");
                break;

            case "rainbow":
                AnsiConsole.MarkupLine("[red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/][red]█[/][orange1]█[/][yellow]█[/][green]█[/][cyan]█[/][blue]█[/][purple]█[/]");
                AnsiConsole.Write(figlet);
                AnsiConsole.MarkupLine("[purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/][purple]█[/][blue]█[/][cyan]█[/][green]█[/][yellow]█[/][orange1]█[/][red]█[/]");
                break;

            default:
                AnsiConsole.Write(figlet);
                break;
        }
    }

    private static void ShowSelectedStyle(string text, int fontIndex, int styleIndex)
    {
        AnsiConsole.Clear();

        var (fontName, fontFile, _) = AvailableFonts[fontIndex];
        var (styleName, color, decoration) = ColorStyles[styleIndex];

        AnsiConsole.MarkupLine($"[green]✓[/] Selected: [bold]{fontName}[/] + [bold]{styleName}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        try
        {
            var font = GetOrDownloadFont(fontIndex);
            RenderWithStyle(text, font, color, decoration);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]───────────────────────────────────────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        // Show configuration details
        AnsiConsole.MarkupLine("[yellow]Configuration:[/]");
        AnsiConsole.MarkupLine($"  [grey]Font file:[/] {fontFile}");
        AnsiConsole.MarkupLine($"  [grey]Color:[/] {color}");
        AnsiConsole.MarkupLine($"  [grey]Decoration:[/] {decoration ?? "none"}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Fonts cached in:[/] {FontCacheDir}");
    }

    private static string GetTerminalInfo()
    {
        var term = Environment.GetEnvironmentVariable("TERM") ?? "unknown";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        var wtSession = Environment.GetEnvironmentVariable("WT_SESSION");
        var conEmu = Environment.GetEnvironmentVariable("ConEmuANSI");

        if (!string.IsNullOrEmpty(wtSession))
            return "[cyan]Windows Terminal[/]";
        if (!string.IsNullOrEmpty(conEmu))
            return "[cyan]ConEmu[/]";
        if (termProgram.Contains("iTerm", StringComparison.OrdinalIgnoreCase))
            return "[cyan]iTerm2[/]";
        if (termProgram.Contains("ghostty", StringComparison.OrdinalIgnoreCase))
            return "[cyan]Ghostty[/]";
        if (termProgram.Contains("vscode", StringComparison.OrdinalIgnoreCase))
            return "[cyan]VS Code Terminal[/]";
        if (term.Contains("xterm"))
            return "[cyan]xterm[/]";
        if (Environment.GetEnvironmentVariable("PSModulePath") != null)
            return "[cyan]PowerShell[/]";

        return $"[grey]{Markup.Escape(term)}[/]";
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Usage:[/] patchsync fonts [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Options:[/]");
        AnsiConsole.MarkupLine("  --text, -t <text>  Text to preview (default: PatchSync)");
        AnsiConsole.MarkupLine("  -h, --help         Show this help message");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Interactive Controls:[/]");
        AnsiConsole.MarkupLine("  ←/→ or h/l    Cycle through fonts");
        AnsiConsole.MarkupLine("  ↑/↓ or j/k    Cycle through styles");
        AnsiConsole.MarkupLine("  PgUp/PgDn     Skip 10 fonts");
        AnsiConsole.MarkupLine("  Space         Random font");
        AnsiConsole.MarkupLine("  Enter         Select current combination");
        AnsiConsole.MarkupLine("  Esc/q         Cancel and exit");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Features:[/]");
        AnsiConsole.MarkupLine($"  {AvailableFonts.Length} Figlet fonts (downloaded on demand)");
        AnsiConsole.MarkupLine($"  {ColorStyles.Length} color/style presets");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  patchsync fonts");
        AnsiConsole.MarkupLine("  patchsync fonts --text \"My App\"");
    }
}
