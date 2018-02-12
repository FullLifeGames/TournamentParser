<html>


<head>
  
  <title>Match Checker</title>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="http://maxcdn.bootstrapcdn.com/bootstrap/3.3.5/css/bootstrap.min.css">
  <script src="https://ajax.googleapis.com/ajax/libs/jquery/1.11.3/jquery.min.js"></script>
  <script src="http://maxcdn.bootstrapcdn.com/bootstrap/3.3.5/js/bootstrap.min.js"></script>

<style>

a {
    color: #337ab7!important;
}

</style>
  
</head>

<body>

	<div class="container">
	
		
		<h1>Match Checker</h1>
				
		<hr />

		<form action="" method="post">
			<div class="form-group">
				<input class="form-control" type="text" placeholder="Smogon User" name="user" />
				<br>
				<input type="submit" value="Send" class="btn btn-primary form-control" />
			</div>
		</form>

		<hr />
		
		<?php
			function matchesSort($a, $b) {
				return strtotime($a->postDate) <= strtotime($b->postDate);
			}
			if(isset($_POST["user"]))
			{
				$json = json_decode(file_get_contents("output.json"));
				
				$user = strtolower(preg_replace("/[, ]/", "", $_POST["user"]));
				echo '<h2 style="text-align:center;">' . trim(htmlentities($_POST["user"])) . ":</h2><hr>";

				$matchesKey = "matches";
				
				if(isset($json->$user))
				{
					$userAccess = $json->$user;
					$matches = $userAccess->$matchesKey;
					usort($matches, 'matchesSort');
					foreach($matches as $match){
						
						echo '<div class="panel panel-default tofilter">
						<div class="panel-heading" style="text-align:center;">
								<h4 class="panel-title">';

						$firstUser = $match->firstUser;
						$firstUserOut = $firstUser;
						
						if(!empty($json->$firstUser)){
							$firstUserObject = $json->$firstUser;
							if($firstUserObject->profileLink != null){
								$firstUserOut = '<a href="' . $firstUserObject->profileLink . '">' . $firstUserOut . '</a>';
							}
							if($match->winner != null && $firstUser == $match->winner){
								$firstUserOut = "<b>" . $firstUserOut . "</b>";
							}
						}
						
						$secondUser = $match->secondUser;
						$secondUserOut = $secondUser;
						
						if(!empty($json->$secondUser)){
							$secondUserObject = $json->$secondUser;
						
							if($secondUserObject->profileLink != null){
								$secondUserOut = '<a href="' . $secondUserObject->profileLink . '">' . $secondUserOut . '</a>';
							}
							if($match->winner != null && $secondUser == $match->winner){
								$secondUserOut = "<b>" . $secondUserOut . "</b>";
							}
						}

						echo (($match->finished) ? '' : '<b>ACTIVE</b>: ') . $firstUserOut . " vs. " . $secondUserOut . " in <a href='" . $match->thread->link . "'>" . $match->thread->name . "</a>" . "<br>";
						
						
						
						echo '</h4></div>';
						if(count($match->replays) > 0){

							echo '<div id="collapse2" class="panel-collapse in collapse">
        <div class="panel-body">';
						
							echo "Replays:<br>";
							
							foreach($match->replays as $replay){
								echo "<a href='" . $replay . "'>" . $replay . "</a><br>";
							}
						
							echo "</div>
								</div>";
						}
						  echo "</div>";
						
					}
				}
			}

		?>
	</div>
</body>

</html>
