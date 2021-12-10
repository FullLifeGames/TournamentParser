<?php
require_once(__DIR__ . '/vendor/autoload.php');

use JsonMachine\JsonDecoder\ExtJsonDecoder;
use JsonMachine\JsonMachine;
?>

<html>

<head>
  
  <title>Match Checker</title>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="/css/bootstrap.min.css">
  <script src="/js/jquery.min.js"></script>
  <script src="/js/bootstrap.min.js"></script>
	<script type="text/javascript" nonce="sUFxWeHDPtMYm5wT0OVeD6k5Qxx0gtTshGwxU4Re8Fs">
		$(function(){
			
			function calculateNewWinrate(){
				var teams = $('.tofilter:visible');
				
				var name = $('#name').text().replace(/[, ]/g, "").toLowerCase();
				var winrate = "Not defined";
				var wonMatches = 0;
				var doneMatches = 0;
				for(var i=0;i<teams.length;i++) {
					var team = teams.eq(i);
					var teamdump = team.children().eq(0);
					if(teamdump != undefined){
						var winner = teamdump.find("b").text();	
						if(winner != "" && winner != "ACTIVE"){
							if(winner == name){
								wonMatches++;
							}
							doneMatches++;
						}
					}
				}
				
				if(doneMatches > 0){
					var winPercentage = (wonMatches / doneMatches) * 100;
					winrate = winPercentage.toFixed(2) + "%";
				}
				$('#winrate').html(winrate);
			}
			
			$('#filter').change(function(){
				var value = $('#filter').val();
				
				var tours = value.split("|");
				for(var i=0;i<tours.length;i++) {
					tours[i] = tours[i].split(",");
					
					for(var j=0;j<tours[i].length;j++) {						
						tours[i][j] = tours[i][j].trim();
						tours[i][j] = tours[i][j].toLowerCase();
					}
				}
				
				var teams = $('.tofilter');
				for(var i=0;i<teams.length;i++) {
					var team = teams.eq(i);
					var teamdump = team.children().eq(0);
					teamdump = teamdump.html().replace(/<.*?>/g, "");
					if(teamdump != undefined){
						teamdump = teamdump.toLowerCase();
						var matches = false;
						for(var j=0;j<tours.length;j++) {
							var tempMatches = true;
							$.each(tours[j], function(key2, mon) {
								if(teamdump.indexOf(mon) === -1){
									tempMatches = false;
								}
							});
							matches = matches || tempMatches;
						}
						if(matches || tours.length == 0){
							team.show();
						} else {
							team.hide();
						}
					}
				}
				calculateNewWinrate();
			});
			
			calculateNewWinrate();
		});
	</script>
  
  
<style>

a {
    color: #337ab7!important;
}

</style>
  
</head>

<body>

	<div class="container">

	        <a href="/" style="position:absolute;top:0;right:0;z-index:20;padding:5px;"><img border="0" alt="FullLifeGames" src="../../img/profile.jpg" width="100" height="100" /></a>	
		
		<h1>Match Checker</h1>
				
		<hr />

		<form action="" method="get">
			<div class="form-group">
				<input class="form-control" type="text" placeholder="Smogon User" name="user" />
				<br>
				<input type="submit" value="Send" class="btn btn-primary form-control" />
			</div>
		</form>

		<hr />
		
		<?php
			date_default_timezone_set("UTC");
			function matchesSort($a, $b) {
				return strtotime($a->p) <= strtotime($b->p);
			}
			function getUser($json, $user) {
				$foundUser = null;
				foreach ($json as $id => $userparsed) {
					if ($user === $id) {
						$foundUser = $userparsed;
						break;
					}
				}
				return $foundUser;
			}
			if(isset($_GET["user"]))
			{
				$json = JsonMachine::fromFile("output.json", '', new ExtJsonDecoder);
				
				$user = strtolower(preg_replace("/[, ]/", "", $_GET["user"]));
				echo '<h2 style="text-align:center;"><name id="name">' . trim(htmlentities($_GET["user"])) . "</name>";				
				
				$matchesKey = "m";
				
				$foundUser = getUser($json, $user);

				if($foundUser !== null)
				{
					$matches = $foundUser->$matchesKey;
					usort($matches, 'matchesSort');
					
					$matchesCount = 0;
					$wonCount = 0;
					foreach($matches as $match){
						if($match->w !== null){
							$matchesCount++;
							if($match->w === $user){
								$wonCount++;
							}
						}
					}
					
					if($matchesCount > 0){
						echo " (Winrate: <winrate id='winrate'>" . round((($wonCount / $matchesCount) * 100), 2) . "%</winrate>)";
					}
					
					echo ":</h2><hr>";
					echo '<input type="text" id="filter" placeholder="Tournament or user filter (e.g. \'Smogon Premier League, Official Smogon Tournament | FullLifeGames\')" class="form-control" /><hr>';
					
					foreach($matches as $match){
						
						echo '<div class="panel panel-default tofilter">
						<div class="panel-heading" style="text-align:center;">
								<h4 class="panel-title">';

						$firstUser = $match->f;
						$firstUserOut = '<a href="https://fulllifegames.com/Tools/MatchChecker/?user=' . $firstUser . '">' . $firstUser . '</a>';
						if($match->w != null && $firstUser == $match->w){
							$firstUserOut = "<b>" . $firstUserOut . "</b>";
						}
						
						$secondUser = $match->s;									
						$secondUserOut = '<a href="https://fulllifegames.com/Tools/MatchChecker/?user=' . $secondUser . '">' . $secondUserOut . '</a>';
						if($match->w != null && $secondUser == $match->w){
							$secondUserOut = "<b>" . $secondUserOut . "</b>";
						}

						echo (($match->f) ? '' : '<b>ACTIVE</b>: ') . $firstUserOut . " vs. " . $secondUserOut . " in <a href='https://www.smogon.com/forums/threads/" . $match->t->i . "/'>" . $match->t->n . "</a>" . "<br>";
						
						
						
						echo '</h4></div>';
						if(count($match->r) > 0){

							echo '<div id="collapse2" class="panel-collapse in collapse">
        <div class="panel-body">';
						
							echo "Replays:<br>";
							
							foreach($match->r as $replay){
								echo "<a href='" . $replay . "'>" . $replay . "</a><br>";
							}
						
							echo "</div>
								</div>";
						}
						  echo "</div>";
						
					}
				} else {
					echo ":</h2><hr>";
				}
			}

		?>
	</div>
</body>

</html>
